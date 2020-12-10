using Stripe;
using Stripe.Checkout;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Web.Mvc;
using Vendr.Core;
using Vendr.Core.Models;
using Vendr.Core.Web;
using Vendr.Core.Web.Api;
using Vendr.Core.Web.PaymentProviders;

namespace Vendr.PaymentProviders.Stripe
{
    [Obsolete("Use the StripeCheckoutPaymentProvider instead")]
    [PaymentProvider("stripe-checkout-onetime", "Stripe Checkout (One Time)", "Stripe Checkout payment provider for one time payments")]
    public class StripeCheckoutOneTimePaymentProvider : StripePaymentProviderBase<StripeCheckoutOneTimeSettings>
    {
        public StripeCheckoutOneTimePaymentProvider(VendrContext vendr)
            : base(vendr)
        { }

        public override bool CanFetchPaymentStatus => true;
        public override bool CanCapturePayments => true;
        public override bool CanCancelPayments => true;
        public override bool CanRefundPayments => true;

        // Don't finalize at continue as we will finalize async via webhook
        public override bool FinalizeAtContinueUrl => false;

        public override IEnumerable<TransactionMetaDataDefinition> TransactionMetaDataDefinitions => new[]{
            new TransactionMetaDataDefinition("stripeSessionId", "Stripe Session ID"),
            new TransactionMetaDataDefinition("stripePaymentIntentId", "Stripe Payment Intent ID"),
            new TransactionMetaDataDefinition("stripeChargeId", "Stripe Charge ID"),
            new TransactionMetaDataDefinition("stripeCardCountry", "Stripe Card Country")
        };

        public override PaymentFormResult GenerateForm(OrderReadOnly order, string continueUrl, string cancelUrl, string callbackUrl, StripeCheckoutOneTimeSettings settings)
        {
            var secretKey = settings.TestMode ? settings.TestSecretKey : settings.LiveSecretKey;
            var publicKey = settings.TestMode ? settings.TestPublicKey : settings.LivePublicKey;

            ConfigureStripe(secretKey);

            var currency = Vendr.Services.CurrencyService.GetCurrency(order.CurrencyId);
            var billingCountry = order.PaymentInfo.CountryId.HasValue
                ? Vendr.Services.CountryService.GetCountry(order.PaymentInfo.CountryId.Value)
                : null;

            var customerOptions = new CustomerCreateOptions
            {
                Name = $"{order.CustomerInfo.FirstName} {order.CustomerInfo.LastName}",
                Email = order.CustomerInfo.Email,
                Description = order.OrderNumber,
                Address = new AddressOptions
                {
                    Line1 = !string.IsNullOrWhiteSpace(settings.BillingAddressLine1PropertyAlias)
                        ? order.Properties[settings.BillingAddressLine1PropertyAlias] : "",
                    Line2 = !string.IsNullOrWhiteSpace(settings.BillingAddressLine1PropertyAlias)
                        ? order.Properties[settings.BillingAddressLine2PropertyAlias] : "",
                    City = !string.IsNullOrWhiteSpace(settings.BillingAddressCityPropertyAlias)
                        ? order.Properties[settings.BillingAddressCityPropertyAlias] : "",
                    State = !string.IsNullOrWhiteSpace(settings.BillingAddressStatePropertyAlias)
                        ? order.Properties[settings.BillingAddressStatePropertyAlias] : "",
                    PostalCode = !string.IsNullOrWhiteSpace(settings.BillingAddressZipCodePropertyAlias)
                        ? order.Properties[settings.BillingAddressZipCodePropertyAlias] : "",
                    Country = billingCountry?.Code
                }
            };

            var customerService = new CustomerService();
            var customer = customerService.Create(customerOptions);

            var sessionOptions = new SessionCreateOptions
            {
                Customer = customer.Id,
                PaymentMethodTypes = new List<string> {
                    "card",
                },
                LineItems = new List<SessionLineItemOptions> {
                    new SessionLineItemOptions {
                        Name = !string.IsNullOrWhiteSpace(settings.OrderHeading) ? settings.OrderHeading : "#" + order.OrderNumber,
                        Description = !string.IsNullOrWhiteSpace(settings.OrderHeading) ? "#" + order.OrderNumber : null,
                        Amount = AmountToMinorUnits(order.TransactionAmount.Value),
                        Currency = currency.Code,
                        Quantity = 1
                    },
                },
                PaymentIntentData = new SessionPaymentIntentDataOptions
                {
                    CaptureMethod = settings.Capture ? "automatic" : "manual",
                    Metadata = new Dictionary<string, string>
                    {
                        { "orderReference", order.GenerateOrderReference() },
                        // Pass billing country / zipecode as meta data as currently
                        // this is the only way it can be validated via Radar
                        // Block if ::orderBillingCountry:: != :card_country:
                        { "orderBillingCountry", billingCountry.Code?.ToUpper() },
                        { "orderBillingZipCode", customerOptions.Address.PostalCode }
                    }
                },
                Mode = "payment",
                ClientReferenceId = order.GenerateOrderReference(),
                SuccessUrl = continueUrl,
                CancelUrl = cancelUrl,
            };

            if (!string.IsNullOrWhiteSpace(settings.OrderImage))
            {
                sessionOptions.LineItems[0].Images = new[] { settings.OrderImage }.ToList();
            }

            if (settings.SendStripeReceipt)
            {
                sessionOptions.PaymentIntentData.ReceiptEmail = order.CustomerInfo.Email;
            }

            var sessionService = new SessionService();
            var session = sessionService.Create(sessionOptions);

            return new PaymentFormResult()
            {
                Form = new PaymentForm(continueUrl, FormMethod.Post)
                    .WithAttribute("onsubmit", "return handleStripeCheckout(event)")
                    .WithJsFile("https://js.stripe.com/v3/")
                    .WithJs(@"
                        var stripe = Stripe('" + publicKey + @"');

                        window.handleStripeCheckout = function (e) {
                            e.preventDefault();
                            stripe.redirectToCheckout({
                                sessionId: '" + session.Id + @"'
                            }).then(function (result) {
                              // If `redirectToCheckout` fails due to a browser or network
                              // error, display the localized error message to your customer
                              // using `result.error.message`.
                            });
                            return false;
                        }
                    ")
            };
        }

        public override CallbackResult ProcessCallback(OrderReadOnly order, HttpRequestBase request, StripeCheckoutOneTimeSettings settings)
        {
            // The ProcessCallback method is only intendid to be called via a Stripe Webhook and so
            // it's job is to process the webhook event and finalize / update the order accordingly

            try
            {
                var secretKey = settings.TestMode ? settings.TestSecretKey : settings.LiveSecretKey;
                var webhookSigningSecret = settings.TestMode ? settings.TestWebhookSigningSecret : settings.LiveWebhookSigningSecret;

                ConfigureStripe(secretKey);

                var stripeEvent = GetWebhookStripeEvent(request, webhookSigningSecret);
                if (stripeEvent != null && stripeEvent.Type == Events.CheckoutSessionCompleted)
                {
                    if (stripeEvent.Data?.Object?.Instance is Session stripeSession)
                    {
                        var paymentIntentService = new PaymentIntentService();
                        var paymentIntent = paymentIntentService.Get(stripeSession.PaymentIntentId);

                        return CallbackResult.Ok(new TransactionInfo
                        {
                            TransactionId = GetTransactionId(paymentIntent),
                            AmountAuthorized = AmountFromMinorUnits(paymentIntent.Amount),
                            PaymentStatus = GetPaymentStatus(paymentIntent)
                        },
                        new Dictionary<string, string>
                        {
                            { "stripeSessionId", stripeSession.Id },
                            { "stripePaymentIntentId", stripeSession.PaymentIntentId },
                            { "stripeChargeId", GetTransactionId(paymentIntent) },
                            { "stripeCardCountry", paymentIntent.Charges?.Data?.FirstOrDefault()?.PaymentMethodDetails?.Card?.Country }
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Vendr.Log.Error<StripeCheckoutOneTimePaymentProvider>(ex, "Stripe - ProcessCallback");
            }

            return CallbackResult.BadRequest();
        }

        public override ApiResult FetchPaymentStatus(OrderReadOnly order, StripeCheckoutOneTimeSettings settings)
        {
            try
            {
                var secretKey = settings.TestMode ? settings.TestSecretKey : settings.LiveSecretKey;

                ConfigureStripe(secretKey);

                // See if we have a payment intent to work from
                var paymentIntentId = order.Properties["stripePaymentIntentId"];
                if (!string.IsNullOrWhiteSpace(paymentIntentId))
                {
                    var paymentIntentService = new PaymentIntentService();
                    var paymentIntent = paymentIntentService.Get(paymentIntentId);

                    return new ApiResult()
                    {
                        TransactionInfo = new TransactionInfoUpdate()
                        {
                            TransactionId = GetTransactionId(paymentIntent),
                            PaymentStatus = GetPaymentStatus(paymentIntent)
                        }
                    };
                }

                // No payment intent, so look for a charge
                var chargeId = order.Properties["stripeChargeId"];
                if (!string.IsNullOrWhiteSpace(chargeId))
                {
                    var chargeService = new ChargeService();
                    var charge = chargeService.Get(chargeId);

                    return new ApiResult()
                    {
                        TransactionInfo = new TransactionInfoUpdate()
                        {
                            TransactionId = GetTransactionId(charge),
                            PaymentStatus = GetPaymentStatus(charge)
                        }
                    };
                }
            }
            catch (Exception ex)
            {
                Vendr.Log.Error<StripeCheckoutOneTimePaymentProvider>(ex, "Stripe - FetchPaymentStatus");
            }

            return ApiResult.Empty;
        }

        public override ApiResult CapturePayment(OrderReadOnly order, StripeCheckoutOneTimeSettings settings)
        {
            try
            {
                // We can only capture a payment intent, so make sure we have one
                // otherwise there is nothing we can do
                var paymentIntentId = order.Properties["stripePaymentIntentId"];
                if (string.IsNullOrWhiteSpace(paymentIntentId))
                    return null;

                var secretKey = settings.TestMode ? settings.TestSecretKey : settings.LiveSecretKey;

                ConfigureStripe(secretKey);

                var paymentIntentService = new PaymentIntentService();
                var paymentIntentOptions = new PaymentIntentCaptureOptions
                {
                    AmountToCapture = AmountToMinorUnits(order.TransactionInfo.AmountAuthorized.Value),
                };
                var paymentIntent = paymentIntentService.Capture(paymentIntentId, paymentIntentOptions);

                return new ApiResult()
                {
                    TransactionInfo = new TransactionInfoUpdate()
                    {
                        TransactionId = GetTransactionId(paymentIntent),
                        PaymentStatus = GetPaymentStatus(paymentIntent)
                    },
                    MetaData = new Dictionary<string, string>
                    {
                        { "stripeChargeId", GetTransactionId(paymentIntent) },
                        { "stripeCardCountry", paymentIntent.Charges?.Data?.FirstOrDefault()?.PaymentMethodDetails?.Card?.Country }
                    }
                };
            }
            catch (Exception ex)
            {
                Vendr.Log.Error<StripeCheckoutOneTimePaymentProvider>(ex, "Stripe - CapturePayment");
            }

            return ApiResult.Empty;
        }

        public override ApiResult RefundPayment(OrderReadOnly order, StripeCheckoutOneTimeSettings settings)
        {
            try
            {
                // We can only refund a captured charge, so make sure we have one
                // otherwise there is nothing we can do
                var chargeId = order.Properties["stripeChargeId"];
                if (string.IsNullOrWhiteSpace(chargeId))
                    return null;

                var secretKey = settings.TestMode ? settings.TestSecretKey : settings.LiveSecretKey;

                ConfigureStripe(secretKey);

                var refundService = new RefundService();
                var refundCreateOptions = new RefundCreateOptions()
                {
                    Charge = chargeId
                };

                var refund = refundService.Create(refundCreateOptions);
                var charge = refund.Charge ?? new ChargeService().Get(refund.ChargeId);

                return new ApiResult()
                {
                    TransactionInfo = new TransactionInfoUpdate()
                    {
                        TransactionId = GetTransactionId(charge),
                        PaymentStatus = GetPaymentStatus(charge)
                    }
                };
            }
            catch (Exception ex)
            {
                Vendr.Log.Error<StripeCheckoutOneTimePaymentProvider>(ex, "Stripe - RefundPayment");
            }

            return ApiResult.Empty;
        }

        public override ApiResult CancelPayment(OrderReadOnly order, StripeCheckoutOneTimeSettings settings)
        {
            try
            {
                // See if there is a payment intent to cancel
                var stripePaymentIntentId = order.Properties["stripePaymentIntentId"];
                if (!string.IsNullOrWhiteSpace(stripePaymentIntentId))
                {
                    var secretKey = settings.TestMode ? settings.TestSecretKey : settings.LiveSecretKey;

                    ConfigureStripe(secretKey);

                    var paymentIntentService = new PaymentIntentService();
                    var intent = paymentIntentService.Cancel(stripePaymentIntentId);

                    return new ApiResult()
                    {
                        TransactionInfo = new TransactionInfoUpdate()
                        {
                            TransactionId = GetTransactionId(intent),
                            PaymentStatus = GetPaymentStatus(intent)
                        }
                    };
                }

                // If there is a charge, then it's too late to cancel
                // so we attempt to refund it instead
                var chargeId = order.Properties["stripeChargeId"];
                if (chargeId != null)
                    return RefundPayment(order, settings);
            }
            catch (Exception ex)
            {
                Vendr.Log.Error<StripeCheckoutOneTimePaymentProvider>(ex, "Stripe - CancelPayment");
            }

            return ApiResult.Empty;
        }

        
    }
}
