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
    [PaymentProvider("stripe-checkout", "Stripe Checkout", "Stripe Checkout payment provider for one time and subscription payments")]
    public class StripeCheckoutPaymentProvider : StripePaymentProviderBase<StripeCheckoutSettings>
    {
        public StripeCheckoutPaymentProvider(VendrContext vendr)
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
            new TransactionMetaDataDefinition("stripeSubscriptionId", "Stripe Subscription ID"),
            new TransactionMetaDataDefinition("stripeChargeId", "Stripe Charge ID"),
            new TransactionMetaDataDefinition("stripeCardCountry", "Stripe Card Country")
        };

        public override PaymentFormResult GenerateForm(OrderReadOnly order, string continueUrl, string cancelUrl, string callbackUrl, StripeCheckoutSettings settings)
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
            
            customerOptions.Metadata = new Dictionary<string, string>
            {
                { "billingCountry", customerOptions.Address.Country },
                { "billingZipCode", customerOptions.Address.PostalCode }
            };

            var customerService = new CustomerService();
            var customer = customerService.Create(customerOptions);

            var hasSubscriptionItems = false;
            long subscriptionsTotalPrice = 0;
            long orderTotalPrice = AmountToMinorUnits(order.TotalPrice.Value.WithTax);

            var lineItems = new List<SessionLineItemOptions>();

            foreach (var orderLine in order.OrderLines.Where(x => x.Properties.ContainsKey("isSubscription")
                && (x.Properties["isSubscription"] == "1" || x.Properties["isSubscription"] == "true" || x.Properties["isSubscription"] == "True")))
            {
                var orderLinePrice = AmountToMinorUnits(orderLine.TotalPrice.Value.WithTax);

                var lineItemOpts = new SessionLineItemOptions();

                if (orderLine.Properties.ContainsKey("stripePriceId") && !string.IsNullOrWhiteSpace(orderLine.Properties["stripePriceId"]))
                {
                    // NB: When using stripe prices there is an inherit risk that values may not
                    // actually be in sync and so the price displayed on the site might not match
                    // that in stripe and so this may cause inconsistant payments
                    lineItemOpts.Price = orderLine.Properties["stripePriceId"].Value;

                    // If we are using a stripe price, then assume the quantity of the line item means
                    // the quantity of the stripe price you want to buy.
                    lineItemOpts.Quantity = (long)orderLine.Quantity;
                }
                else
                {
                    // We don't have a stripe price defined on the order line
                    // so we'll create one on the fly using th order lines total
                    // value
                    var priceData = new SessionLineItemPriceDataOptions
                    {
                        Currency = currency.Code,
                        UnitAmount = orderLinePrice,
                        Recurring = new SessionLineItemPriceDataRecurringOptions
                        {
                            Interval = orderLine.Properties["stripeSubscriptionInterval"].Value.ToLower(),
                            IntervalCount = long.TryParse(orderLine.Properties["stripeSubscriptionIntervalCount"], out var intervalCount) ? intervalCount : 1
                        }
                    };

                    if (orderLine.Properties.ContainsKey("stripeProductId") && !string.IsNullOrWhiteSpace(orderLine.Properties["stripeProductId"]))
                    {
                        priceData.Product = orderLine.Properties["stripeProductId"].Value;
                    }
                    else
                    {
                        priceData.ProductData = new SessionLineItemPriceDataProductDataOptions
                        {
                            Name = orderLine.Name,
                            Metadata = new Dictionary<string, string>
                            {
                                { "ProductReference", orderLine.ProductReference }
                            }
                        };
                    }

                    lineItemOpts.PriceData = priceData;

                    // For dynamic subscriptions, regardless of line item quantity, treat the line
                    // as a single subscription item with one price being the line items total price
                    lineItemOpts.Quantity = 1;
                }

                lineItems.Add(lineItemOpts);

                subscriptionsTotalPrice += orderLinePrice;
                hasSubscriptionItems = true;
            }

            if (subscriptionsTotalPrice < orderTotalPrice)
            {
                // If the total value of the order is not covered by the subscription items
                // then we add another line item for the remainder of the order value

                var lineItemOpts = new SessionLineItemOptions
                {
                    PriceData = new SessionLineItemPriceDataOptions
                    {
                        Currency = currency.Code,
                        UnitAmount = orderTotalPrice - subscriptionsTotalPrice,
                        ProductData = new SessionLineItemPriceDataProductDataOptions
                        {
                            Name = hasSubscriptionItems
                                ? !string.IsNullOrWhiteSpace(settings.OneTimeItemsHeading) ? settings.OneTimeItemsHeading : "One time items"
                                : !string.IsNullOrWhiteSpace(settings.OrderHeading) ? settings.OrderHeading : "#" + order.OrderNumber,
                            Description = hasSubscriptionItems || string.IsNullOrWhiteSpace(settings.OrderHeading) ? null : "#" + order.OrderNumber,
                        }
                    },
                    Quantity = 1
                };

                // Only add the order image if this is a one time only payment
                if (!hasSubscriptionItems && !string.IsNullOrWhiteSpace(settings.OrderImage))
                {
                    lineItemOpts.PriceData.ProductData.Images = new[] { settings.OrderImage }.ToList();
                }

                lineItems.Add(lineItemOpts);
            }

            var sessionOptions = new SessionCreateOptions
            {
                Customer = customer.Id,
                PaymentMethodTypes = new List<string> {
                    "card",
                },
                LineItems = lineItems,
                Mode = hasSubscriptionItems 
                    ? "subscription"
                    : "payment",
                ClientReferenceId = order.GenerateOrderReference(),
                SuccessUrl = continueUrl,
                CancelUrl = cancelUrl,
            };

            if (hasSubscriptionItems)
            {
                sessionOptions.SubscriptionData = new SessionSubscriptionDataOptions
                {
                    Metadata = new Dictionary<string, string>
                    {
                        { "orderReference", order.GenerateOrderReference() },
                        { "orderId", order.Id.ToString("D") },
                        { "orderNumber", order.OrderNumber },
                        // Pass billing country / zipecode as meta data as currently
                        // this is the only way it can be validated via Radar
                        // Block if ::orderBillingCountry:: != :card_country:
                        { "orderBillingCountry", billingCountry.Code?.ToUpper() },
                        { "orderBillingZipCode", customerOptions.Address.PostalCode }
                    }
                };
            }
            else
            {
                sessionOptions.PaymentIntentData = new SessionPaymentIntentDataOptions
                {
                    CaptureMethod = settings.Capture ? "automatic" : "manual",
                    Metadata = new Dictionary<string, string>
                    {
                        { "orderReference", order.GenerateOrderReference() },
                        { "orderId", order.Id.ToString("D") },
                        { "orderNumber", order.OrderNumber },
                        // Pass billing country / zipecode as meta data as currently
                        // this is the only way it can be validated via Radar
                        // Block if ::orderBillingCountry:: != :card_country:
                        { "orderBillingCountry", billingCountry.Code?.ToUpper() },
                        { "orderBillingZipCode", customerOptions.Address.PostalCode }
                    }
                };
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

        public override CallbackResult ProcessCallback(OrderReadOnly order, HttpRequestBase request, StripeCheckoutSettings settings)
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
                        if (stripeSession.Mode == "payment")
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
                                { "stripeSubscriptionId", stripeSession.SubscriptionId },
                                { "stripeChargeId", GetTransactionId(paymentIntent) },
                                { "stripeCardCountry", paymentIntent.Charges?.Data?.FirstOrDefault()?.PaymentMethodDetails?.Card?.Country }
                            });
                        }
                        else if (stripeSession.Mode == "subscription")
                        {
                            var subscriptionService = new SubscriptionService();
                            var subscription = subscriptionService.Get(stripeSession.SubscriptionId, new SubscriptionGetOptions { 
                                Expand = new List<string>(new[] { 
                                    "latest_invoice",
                                    "latest_invoice.charge",
                                    "latest_invoice.payment_intent"
                                })
                            });
                            var invoice = subscription.LatestInvoice;

                            return CallbackResult.Ok(new TransactionInfo
                            {
                                TransactionId = GetTransactionId(invoice),
                                AmountAuthorized = AmountFromMinorUnits(invoice.PaymentIntent.Amount),
                                PaymentStatus = GetPaymentStatus(invoice)
                            },
                            new Dictionary<string, string>
                            {
                                { "stripeSessionId", stripeSession.Id },
                                { "stripePaymentIntentId", invoice.PaymentIntentId },
                                { "stripeSubscriptionId", stripeSession.SubscriptionId },
                                { "stripeChargeId", invoice.ChargeId },
                                { "stripeCardCountry", invoice.Charge?.PaymentMethodDetails?.Card?.Country }
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Vendr.Log.Error<StripeCheckoutOneTimePaymentProvider>(ex, "Stripe - ProcessCallback");
            }

            return CallbackResult.BadRequest();
        }

        public override ApiResult FetchPaymentStatus(OrderReadOnly order, StripeCheckoutSettings settings)
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

        public override ApiResult CapturePayment(OrderReadOnly order, StripeCheckoutSettings settings)
        {
            // NOTE: Subscriptions aren't currently abled to be "authorized" so the capture
            // routine shouldn't be relevant for subscription payments at this point

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

        public override ApiResult RefundPayment(OrderReadOnly order, StripeCheckoutSettings settings)
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

                // If we have a subscription then we'll cancel it as refunding an order
                // should effecitvely undo any purchase
                if (!string.IsNullOrWhiteSpace(order.Properties["stripeSubscriptionId"]))
                {
                    var subscriptionService = new SubscriptionService();
                    subscriptionService.Cancel(order.Properties["stripeSubscriptionId"], new SubscriptionCancelOptions
                    {
                        InvoiceNow = false,
                        Prorate = false
                    });
                }

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

        public override ApiResult CancelPayment(OrderReadOnly order, StripeCheckoutSettings settings)
        {
            // NOTE: Subscriptions aren't currently abled to be "authorized" so the cancel
            // routine shouldn't be relevant for subscription payments at this point

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
