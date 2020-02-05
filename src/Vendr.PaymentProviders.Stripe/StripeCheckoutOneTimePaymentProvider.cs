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

        public override OrderReference GetOrderReference(HttpRequestBase request, StripeCheckoutOneTimeSettings settings)
        {
            try
            {
                var secretKey = settings.Mode == StripePaymentProviderMode.Test ? settings.TestSecretKey : settings.LiveSecretKey;
                var webhookSigningSecret = settings.Mode == StripePaymentProviderMode.Test ? settings.TestWebhookSigningSecret : settings.LiveWebhookSigningSecret;

                ConfigureStripe(secretKey);

                var stripeEvent = GetWebhookStripeEvent(request, webhookSigningSecret);
                if (stripeEvent != null && stripeEvent.Type == Events.CheckoutSessionCompleted)
                {
                    if (stripeEvent.Data?.Object?.Instance is Session stripeSession && !string.IsNullOrWhiteSpace(stripeSession.ClientReferenceId))
                    {
                        return OrderReference.Parse(stripeSession.ClientReferenceId);
                    }
                }
            }
            catch (Exception ex)
            {
                Vendr.Log.Error<StripeCheckoutOneTimePaymentProvider>(ex, "Stripe - GetOrderReference");
            }

            return base.GetOrderReference(request, settings);
        }

        public override PaymentFormResult GenerateForm(OrderReadOnly order, string continueUrl, string cancelUrl, string callbackUrl, StripeCheckoutOneTimeSettings settings)
        {
            var secretKey = settings.Mode == StripePaymentProviderMode.Test ? settings.TestSecretKey : settings.LiveSecretKey;
            var publicKey = settings.Mode == StripePaymentProviderMode.Test ? settings.TestPublicKey : settings.LivePublicKey;

            ConfigureStripe(secretKey);

            var currency = Vendr.Services.CurrencyService.GetCurrency(order.CurrencyId);

            var sessionOptions = new SessionCreateOptions
            {
                CustomerEmail = order.CustomerInfo.Email,
                PaymentMethodTypes = new List<string> {
                    "card",
                },
                LineItems = new List<SessionLineItemOptions> {
                    new SessionLineItemOptions {
                        Name = $"#{order.OrderNumber}",
                        Amount = DollarsToCents(order.TotalPrice.Value.WithTax),
                        Currency = currency.Code,
                        Quantity = 1
                    },
                },
                PaymentIntentData = new SessionPaymentIntentDataOptions
                {
                    CaptureMethod = settings.Capture ? "automatic" : "manual",
                    Metadata = new Dictionary<string, string>
                    {
                        { "orderReference", order.GenerateOrderReference() }
                    }
                },
                ClientReferenceId = order.GenerateOrderReference(),
                SuccessUrl = continueUrl,
                CancelUrl = cancelUrl,
            };

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
                var secretKey = settings.Mode == StripePaymentProviderMode.Test ? settings.TestSecretKey : settings.LiveSecretKey;
                var webhookSigningSecret = settings.Mode == StripePaymentProviderMode.Test ? settings.TestWebhookSigningSecret : settings.LiveWebhookSigningSecret;

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
                            AmountAuthorized = CentsToDollars(paymentIntent.Amount.Value),
                            PaymentStatus = GetPaymentStatus(paymentIntent)
                        },
                        new Dictionary<string, string>
                        {
                            { "stripeSessionId", stripeSession.Id },
                            { "stripePaymentIntentId", stripeSession.PaymentIntentId },
                            { "stripeChargeId", GetTransactionId(paymentIntent) }
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
                var secretKey = settings.Mode == StripePaymentProviderMode.Test ? settings.TestSecretKey : settings.LiveSecretKey;

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

                var secretKey = settings.Mode == StripePaymentProviderMode.Test ? settings.TestSecretKey : settings.LiveSecretKey;

                ConfigureStripe(secretKey);

                var paymentIntentService = new PaymentIntentService();
                var paymentIntentOptions = new PaymentIntentCaptureOptions
                {
                    AmountToCapture = DollarsToCents(order.TransactionInfo.AmountAuthorized.Value),
                };
                var paymentIntent = paymentIntentService.Capture(paymentIntentId, paymentIntentOptions);

                return new ApiResult()
                {
                    TransactionInfo = new TransactionInfoUpdate()
                    {
                        TransactionId = GetTransactionId(paymentIntent),
                        PaymentStatus = GetPaymentStatus(paymentIntent)
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

                var secretKey = settings.Mode == StripePaymentProviderMode.Test ? settings.TestSecretKey : settings.LiveSecretKey;

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
                    var secretKey = settings.Mode == StripePaymentProviderMode.Test ? settings.TestSecretKey : settings.LiveSecretKey;

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

        protected string GetTransactionId(PaymentIntent paymentIntent)
        {
            return (paymentIntent.Charges?.Data?.Count ?? 0) > 0
                ? GetTransactionId(paymentIntent.Charges.Data[0])
                : null;
        }

        protected string GetTransactionId(Charge charge)
        {
            return charge?.Id;
        }

        protected PaymentStatus GetPaymentStatus(PaymentIntent paymentIntent)
        {
            // Possible PaymentIntent statuses:
            // - requires_payment_method
            // - requires_confirmation
            // - requires_action
            // - processing
            // - requires_capture
            // - canceled
            // - succeeded

            if (paymentIntent.Status == "canceled")
                return PaymentStatus.Cancelled;

            if (paymentIntent.Status == "requires_capture")
                return PaymentStatus.Authorized;

            if (paymentIntent.Status == "succeeded")
            {
                if (paymentIntent.Charges.Data.Any())
                {
                    return GetPaymentStatus(paymentIntent.Charges.Data[0]);
                }
                else
                {
                    return PaymentStatus.Captured;
                }
            }

            return PaymentStatus.Initialized;
        }

        protected PaymentStatus GetPaymentStatus(Charge charge)
        {
            PaymentStatus paymentState = PaymentStatus.Initialized;

            if (charge == null)
                return paymentState;

            if (charge.Paid)
            {
                paymentState = PaymentStatus.Authorized;

                if (charge.Captured != null && charge.Captured.Value)
                {
                    paymentState = PaymentStatus.Captured;

                    if (charge.Refunded)
                    {
                        paymentState = PaymentStatus.Refunded;
                    }
                }
                else
                {
                    if (charge.Refunded)
                    {
                        paymentState = PaymentStatus.Cancelled;
                    }
                }
            }

            return paymentState;
        }
    }
}
