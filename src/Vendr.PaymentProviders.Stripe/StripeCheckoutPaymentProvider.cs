using Stripe;
using Stripe.Checkout;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Vendr.Common.Logging;
using Vendr.Core;
using Vendr.Core.Api;
using Vendr.Core.Models;
using Vendr.Core.PaymentProviders;
using Vendr.Extensions;

namespace Vendr.PaymentProviders.Stripe
{
    [PaymentProvider("stripe-checkout", "Stripe Checkout", "Stripe Checkout payment provider for one time and subscription payments")]
    public class StripeCheckoutPaymentProvider : StripePaymentProviderBase<StripeCheckoutPaymentProvider, StripeCheckoutSettings>
    {
        public StripeCheckoutPaymentProvider(VendrContext vendr, ILogger<StripeCheckoutPaymentProvider> logger)
            : base(vendr, logger)
        { }

        public override bool CanFetchPaymentStatus => true;
        public override bool CanCapturePayments => true;
        public override bool CanCancelPayments => true;
        public override bool CanRefundPayments => true;

        // Don't finalize at continue as we will finalize async via webhook
        public override bool FinalizeAtContinueUrl => false;

        public override IEnumerable<TransactionMetaDataDefinition> TransactionMetaDataDefinitions => new[]{
            new TransactionMetaDataDefinition("stripeSessionId", "Stripe Session ID"),
            new TransactionMetaDataDefinition("stripeCustomerId", "Stripe Customer ID"),
            new TransactionMetaDataDefinition("stripePaymentIntentId", "Stripe Payment Intent ID"),
            new TransactionMetaDataDefinition("stripeSubscriptionId", "Stripe Subscription ID"),
            new TransactionMetaDataDefinition("stripeChargeId", "Stripe Charge ID"),
            new TransactionMetaDataDefinition("stripeCardCountry", "Stripe Card Country")
        };

        public override async Task<PaymentFormResult> GenerateFormAsync(PaymentProviderContext<StripeCheckoutSettings> ctx)
        {
            var secretKey = ctx.Settings.TestMode ? ctx.Settings.TestSecretKey : ctx.Settings.LiveSecretKey;
            var publicKey = ctx.Settings.TestMode ? ctx.Settings.TestPublicKey : ctx.Settings.LivePublicKey;

            ConfigureStripe(secretKey);

            var currency = Vendr.Services.CurrencyService.GetCurrency(ctx.Order.CurrencyId);
            var billingCountry = ctx.Order.PaymentInfo.CountryId.HasValue
                ? Vendr.Services.CountryService.GetCountry(ctx.Order.PaymentInfo.CountryId.Value)
                : null;

            Customer customer;
            var customerService = new CustomerService();

            // If we've created a customer already, keep using it but update it incase
            // any of the billing details have changed
            if (!string.IsNullOrWhiteSpace(ctx.Order.Properties["stripeCustomerId"]))
            {
                var customerOptions = new CustomerUpdateOptions
                {
                    Name = $"{ctx.Order.CustomerInfo.FirstName} {ctx.Order.CustomerInfo.LastName}",
                    Email = ctx.Order.CustomerInfo.Email,
                    Description = ctx.Order.OrderNumber,
                    Address = new AddressOptions
                    {
                        Line1 = !string.IsNullOrWhiteSpace(ctx.Settings.BillingAddressLine1PropertyAlias)
                            ? ctx.Order.Properties[ctx.Settings.BillingAddressLine1PropertyAlias] : "",
                        Line2 = !string.IsNullOrWhiteSpace(ctx.Settings.BillingAddressLine2PropertyAlias)
                            ? ctx.Order.Properties[ctx.Settings.BillingAddressLine2PropertyAlias] : "",
                        City = !string.IsNullOrWhiteSpace(ctx.Settings.BillingAddressCityPropertyAlias)
                            ? ctx.Order.Properties[ctx.Settings.BillingAddressCityPropertyAlias] : "",
                        State = !string.IsNullOrWhiteSpace(ctx.Settings.BillingAddressStatePropertyAlias)
                            ? ctx.Order.Properties[ctx.Settings.BillingAddressStatePropertyAlias] : "",
                        PostalCode = !string.IsNullOrWhiteSpace(ctx.Settings.BillingAddressZipCodePropertyAlias)
                            ? ctx.Order.Properties[ctx.Settings.BillingAddressZipCodePropertyAlias] : "",
                        Country = billingCountry?.Code
                    }
                };

                // Pass billing country / zipcode as meta data as currently
                // this is the only way it can be validated via Radar
                // Block if ::customer:billingCountry:: != :card_country:
                customerOptions.Metadata = new Dictionary<string, string>
                {
                    { "billingCountry", customerOptions.Address.Country },
                    { "billingZipCode", customerOptions.Address.PostalCode }
                };

                customer = customerService.Update(ctx.Order.Properties["stripeCustomerId"].Value, customerOptions);
            }
            else
            {
                var customerOptions = new CustomerCreateOptions
                {
                    Name = $"{ctx.Order.CustomerInfo.FirstName} {ctx.Order.CustomerInfo.LastName}",
                    Email = ctx.Order.CustomerInfo.Email,
                    Description = ctx.Order.OrderNumber,
                    Address = new AddressOptions
                    {
                        Line1 = !string.IsNullOrWhiteSpace(ctx.Settings.BillingAddressLine1PropertyAlias)
                        ? ctx.Order.Properties[ctx.Settings.BillingAddressLine1PropertyAlias] : "",
                        Line2 = !string.IsNullOrWhiteSpace(ctx.Settings.BillingAddressLine2PropertyAlias)
                        ? ctx.Order.Properties[ctx.Settings.BillingAddressLine2PropertyAlias] : "",
                        City = !string.IsNullOrWhiteSpace(ctx.Settings.BillingAddressCityPropertyAlias)
                        ? ctx.Order.Properties[ctx.Settings.BillingAddressCityPropertyAlias] : "",
                        State = !string.IsNullOrWhiteSpace(ctx.Settings.BillingAddressStatePropertyAlias)
                        ? ctx.Order.Properties[ctx.Settings.BillingAddressStatePropertyAlias] : "",
                        PostalCode = !string.IsNullOrWhiteSpace(ctx.Settings.BillingAddressZipCodePropertyAlias)
                        ? ctx.Order.Properties[ctx.Settings.BillingAddressZipCodePropertyAlias] : "",
                        Country = billingCountry?.Code
                    }
                };

                // Pass billing country / zipcode as meta data as currently
                // this is the only way it can be validated via Radar
                // Block if ::customer:billingCountry:: != :card_country:
                customerOptions.Metadata = new Dictionary<string, string>
                {
                    { "billingCountry", customerOptions.Address.Country },
                    { "billingZipCode", customerOptions.Address.PostalCode }
                };

                customer = customerService.Create(customerOptions);
            }

            var metaData = new Dictionary<string, string>
            {
                { "orderReference", ctx.Order.GenerateOrderReference() },
                { "orderId", ctx.Order.Id.ToString("D") },
                { "orderNumber", ctx.Order.OrderNumber }
            };

            if (!string.IsNullOrWhiteSpace(ctx.Settings.OrderProperties))
            {
                foreach (var alias in ctx.Settings.OrderProperties.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x)))
                {
                    if (!string.IsNullOrWhiteSpace(ctx.Order.Properties[alias]))
                    {
                        metaData.Add(alias, ctx.Order.Properties[alias]);
                    }
                }
            }

            var hasRecurringItems = false;
            long recurringTotalPrice = 0;
            long orderTotalPrice = AmountToMinorUnits(ctx.Order.TransactionAmount.Value);

            var lineItems = new List<SessionLineItemOptions>();

            foreach (var orderLine in ctx.Order.OrderLines.Where(IsRecurringOrderLine))
            {
                var orderLineTaxRate = orderLine.TaxRate * 100;

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

                    // Because we are in charge of what taxes apply, we need to setup a tax rate
                    // to ensure the price defined in stripe has the relevant taxes applied
                    var stripePricesIncludeTax = PropertyIsTrue(orderLine.Properties, "stripePriceIncludesTax");
                    var stripeTaxRate = GetOrCreateStripeTaxRate(ctx, "Subscription Tax", orderLineTaxRate, stripePricesIncludeTax);
                    if (stripeTaxRate != null)
                    {
                        lineItemOpts.TaxRates = new List<string>(new[] { stripeTaxRate.Id });
                    }
                }
                else
                {
                    // We don't have a stripe price defined on the ctx.Order line
                    // so we'll create one on the fly using the ctx.Order lines total
                    // value
                    var priceData = new SessionLineItemPriceDataOptions
                    {
                        Currency = currency.Code,
                        UnitAmount = AmountToMinorUnits(orderLine.TotalPrice.Value.WithoutTax / orderLine.Quantity), // Without tax as Stripe will apply the tax
                        Recurring = new SessionLineItemPriceDataRecurringOptions
                        {
                            Interval = orderLine.Properties["stripeRecurringInterval"].Value.ToLower(),
                            IntervalCount = long.TryParse(orderLine.Properties["stripeRecurringIntervalCount"], out var intervalCount) ? intervalCount : 1
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
                                { "productReference", orderLine.ProductReference }
                            }
                        };
                    }

                    lineItemOpts.PriceData = priceData;

                    // For dynamic subscriptions, regardless of line item quantity, treat the line
                    // as a single subscription item with one price being the line items total price
                    lineItemOpts.Quantity = (long)orderLine.Quantity;

                    // If we define the price, then create tax rates that are set to be inclusive
                    // as this means that we can pass prices inclusive of tax and Stripe works out
                    // the pre-tax price which would be less suseptable to rounding inconsistancies
                    var stripeTaxRate = GetOrCreateStripeTaxRate(ctx, "Subscription Tax", orderLineTaxRate, false);
                    if (stripeTaxRate != null)
                    {
                        lineItemOpts.TaxRates = new List<string>(new[] { stripeTaxRate.Id });
                    }
                }

                lineItems.Add(lineItemOpts);

                recurringTotalPrice += AmountToMinorUnits(orderLine.TotalPrice.Value.WithTax);
                hasRecurringItems = true;
            }

            if (recurringTotalPrice < orderTotalPrice)
            {
                // If the total value of the ctx.Order is not covered by the subscription items
                // then we add another line item for the remainder of the ctx.Order value

                var lineItemOpts = new SessionLineItemOptions
                {
                    PriceData = new SessionLineItemPriceDataOptions
                    {
                        Currency = currency.Code,
                        UnitAmount = orderTotalPrice - recurringTotalPrice,
                        ProductData = new SessionLineItemPriceDataProductDataOptions
                        {
                            Name = hasRecurringItems
                                ? !string.IsNullOrWhiteSpace(ctx.Settings.OneTimeItemsHeading) ? ctx.Settings.OneTimeItemsHeading : "One time items (inc Tax)"
                                : !string.IsNullOrWhiteSpace(ctx.Settings.OrderHeading) ? ctx.Settings.OrderHeading : "#" + ctx.Order.OrderNumber,
                            Description = hasRecurringItems || !string.IsNullOrWhiteSpace(ctx.Settings.OrderHeading) ? "#" + ctx.Order.OrderNumber : null,
                        }
                    },
                    Quantity = 1
                };

                lineItems.Add(lineItemOpts);
            }
            
            // Add image to the first item (only if it's not a product link)
            if (!string.IsNullOrWhiteSpace(ctx.Settings.OrderImage) && lineItems.Count > 0 && lineItems[0].PriceData?.ProductData != null)
            {
                lineItems[0].PriceData.ProductData.Images = new[] { ctx.Settings.OrderImage }.ToList();
            }

            var sessionOptions = new SessionCreateOptions
            {
                Customer = customer.Id,
                PaymentMethodTypes = !string.IsNullOrWhiteSpace(ctx.Settings.PaymentMethodTypes)
                    ? ctx.Settings.PaymentMethodTypes.Split(',')
                        .Select(tag => tag.Trim())
                        .Where(tag => !string.IsNullOrEmpty(tag))
                        .ToList()
                    : new List<string> {
                        "card",
                    },
                LineItems = lineItems,
                Mode = hasRecurringItems 
                    ? "subscription"
                    : "payment",
                ClientReferenceId = ctx.Order.GenerateOrderReference(),
                SuccessUrl = ctx.Urls.ContinueUrl,
                CancelUrl = ctx.Urls.CancelUrl,
                Locale = FindBestMatchSupportedLocale(ctx.Order.LanguageIsoCode)
            };

            if (hasRecurringItems)
            {
                sessionOptions.SubscriptionData = new SessionSubscriptionDataOptions
                {
                    Metadata = metaData
                };
            }
            else
            {
                sessionOptions.PaymentIntentData = new SessionPaymentIntentDataOptions
                {
                    CaptureMethod = ctx.Settings.Capture ? "automatic" : "manual",
                    Metadata = metaData
                };
            }

            if (ctx.Settings.SendStripeReceipt)
            {
                sessionOptions.PaymentIntentData.ReceiptEmail = ctx.Order.CustomerInfo.Email;
            }

            var sessionService = new SessionService();
            var session = await sessionService.CreateAsync(sessionOptions);

            return new PaymentFormResult()
            {
                MetaData = new Dictionary<string, string>
                {
                    { "stripeSessionId", session.Id },
                    { "stripeCustomerId", session.CustomerId }
                },
                Form = new PaymentForm(ctx.Urls.ContinueUrl, PaymentFormMethod.Post)
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

        public override async Task<CallbackResult> ProcessCallbackAsync(PaymentProviderContext<StripeCheckoutSettings> ctx)
        {
            // The ProcessCallback method is only intendid to be called via a Stripe Webhook and so
            // it's job is to process the webhook event and finalize / update the ctx.Order accordingly

            try
            {
                var secretKey = ctx.Settings.TestMode ? ctx.Settings.TestSecretKey : ctx.Settings.LiveSecretKey;
                var webhookSigningSecret = ctx.Settings.TestMode ? ctx.Settings.TestWebhookSigningSecret : ctx.Settings.LiveWebhookSigningSecret;

                ConfigureStripe(secretKey);

                var stripeEvent = await GetWebhookStripeEventAsync(ctx, webhookSigningSecret);
                if (stripeEvent != null && stripeEvent.Type == Events.CheckoutSessionCompleted)
                {
                    if (stripeEvent.Data?.Object?.Instance is Session stripeSession)
                    {
                        if (stripeSession.Mode == "payment")
                        {
                            var paymentIntentService = new PaymentIntentService();
                            var paymentIntent = await paymentIntentService.GetAsync(stripeSession.PaymentIntentId, new PaymentIntentGetOptions
                            {
                                Expand = new List<string>(new[]
                                {
                                    "latest_charge",
                                    "review"
                                })
                            });

                            return CallbackResult.Ok(new TransactionInfo
                            {
                                TransactionId = GetTransactionId(paymentIntent),
                                AmountAuthorized = AmountFromMinorUnits(paymentIntent.Amount),
                                PaymentStatus = GetPaymentStatus(paymentIntent)
                            },
                            new Dictionary<string, string>
                            {
                                { "stripeSessionId", stripeSession.Id },
                                { "stripeCustomerId", stripeSession.CustomerId },
                                { "stripePaymentIntentId", stripeSession.PaymentIntentId },
                                { "stripeSubscriptionId", stripeSession.SubscriptionId },
                                { "stripeChargeId", GetTransactionId(paymentIntent) },
                                { "stripeCardCountry", paymentIntent.LatestCharge?.PaymentMethodDetails?.Card?.Country }
                            });
                        }
                        else if (stripeSession.Mode == "subscription")
                        {
                            var subscriptionService = new SubscriptionService();
                            var subscription = await subscriptionService.GetAsync(stripeSession.SubscriptionId, new SubscriptionGetOptions
                            { 
                                Expand = new List<string>(new[]
                                { 
                                    "latest_invoice",
                                    "latest_invoice.charge",
                                    "latest_invoice.charge.review",
                                    "latest_invoice.payment_intent",
                                    "latest_invoice.payment_intent.review"
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
                                { "stripeCustomerId", stripeSession.CustomerId },
                                { "stripePaymentIntentId", invoice.PaymentIntentId },
                                { "stripeSubscriptionId", stripeSession.SubscriptionId },
                                { "stripeChargeId", invoice.ChargeId },
                                { "stripeCardCountry", invoice.Charge?.PaymentMethodDetails?.Card?.Country }
                            });
                        }
                    }
                    else if (stripeEvent != null && stripeEvent.Type == Events.ReviewClosed)
                    {
                        if (stripeEvent.Data?.Object?.Instance is Review stripeReview && !string.IsNullOrWhiteSpace(stripeReview.PaymentIntentId))
                        {
                            var paymentIntentService = new PaymentIntentService();
                            var paymentIntent = paymentIntentService.Get(stripeReview.PaymentIntentId, new PaymentIntentGetOptions
                            {
                                Expand = new List<string>(new[]
                                {
                                    "latest_charge",
                                    "review"
                                })
                            });

                            return CallbackResult.Ok(new TransactionInfo
                            {
                                TransactionId = GetTransactionId(paymentIntent),
                                AmountAuthorized = AmountFromMinorUnits(paymentIntent.Amount),
                                PaymentStatus = GetPaymentStatus(paymentIntent)
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Stripe - ProcessCallback");
            }

            return CallbackResult.BadRequest();
        }

        public override async Task<ApiResult> FetchPaymentStatusAsync(PaymentProviderContext<StripeCheckoutSettings> ctx)
        {
            try
            {
                var secretKey = ctx.Settings.TestMode ? ctx.Settings.TestSecretKey : ctx.Settings.LiveSecretKey;

                ConfigureStripe(secretKey);

                // See if we have a payment intent to work from
                var paymentIntentId = ctx.Order.Properties["stripePaymentIntentId"];
                if (!string.IsNullOrWhiteSpace(paymentIntentId))
                {
                    var paymentIntentService = new PaymentIntentService();
                    var paymentIntent = await paymentIntentService.GetAsync(paymentIntentId, new PaymentIntentGetOptions
                    {
                        Expand = new List<string>(new[]
                        {
                            "latest_charge",
                            "review"
                        })
                    });

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
                var chargeId = ctx.Order.Properties["stripeChargeId"];
                if (!string.IsNullOrWhiteSpace(chargeId))
                {
                    var chargeService = new ChargeService();
                    var charge = await chargeService.GetAsync(chargeId);

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
                _logger.Error(ex, "Stripe - FetchPaymentStatus");
            }

            return ApiResult.Empty;
        }

        public override async Task<ApiResult> CapturePaymentAsync(PaymentProviderContext<StripeCheckoutSettings> ctx)
        {
            // NOTE: Subscriptions aren't currently abled to be "authorized" so the capture
            // routine shouldn't be relevant for subscription payments at this point

            try
            {
                // We can only capture a payment intent, so make sure we have one
                // otherwise there is nothing we can do
                var paymentIntentId = ctx.Order.Properties["stripePaymentIntentId"];
                if (string.IsNullOrWhiteSpace(paymentIntentId))
                    return null;

                var secretKey = ctx.Settings.TestMode ? ctx.Settings.TestSecretKey : ctx.Settings.LiveSecretKey;

                ConfigureStripe(secretKey);

                var paymentIntentService = new PaymentIntentService();
                var paymentIntentOptions = new PaymentIntentCaptureOptions
                {
                    AmountToCapture = AmountToMinorUnits(ctx.Order.TransactionInfo.AmountAuthorized.Value)
                };
                var paymentIntent = await paymentIntentService.CaptureAsync(paymentIntentId, paymentIntentOptions);

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
                        { "stripeCardCountry", paymentIntent.LatestCharge?.PaymentMethodDetails?.Card?.Country }
                    }
                };
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Stripe - CapturePayment");
            }

            return ApiResult.Empty;
        }

        public override async Task<ApiResult> RefundPaymentAsync(PaymentProviderContext<StripeCheckoutSettings> ctx)
        {
            try
            {
                // We can only refund a captured charge, so make sure we have one
                // otherwise there is nothing we can do
                var chargeId = ctx.Order.Properties["stripeChargeId"];
                if (string.IsNullOrWhiteSpace(chargeId))
                    return null;

                var secretKey = ctx.Settings.TestMode ? ctx.Settings.TestSecretKey : ctx.Settings.LiveSecretKey;

                ConfigureStripe(secretKey);

                var refundService = new RefundService();
                var refundCreateOptions = new RefundCreateOptions()
                {
                    Charge = chargeId
                };

                var refund = refundService.Create(refundCreateOptions);
                var charge = refund.Charge ?? await new ChargeService().GetAsync(refund.ChargeId);

                // If we have a subscription then we'll cancel it as refunding an ctx.Order
                // should effecitvely undo any purchase
                if (!string.IsNullOrWhiteSpace(ctx.Order.Properties["stripeSubscriptionId"]))
                {
                    var subscriptionService = new SubscriptionService();
                    var subscription = await subscriptionService.GetAsync(ctx.Order.Properties["stripeSubscriptionId"]);
                    if (subscription != null)
                    {
                        subscriptionService.Cancel(ctx.Order.Properties["stripeSubscriptionId"], new SubscriptionCancelOptions
                        {
                            InvoiceNow = false,
                            Prorate = false
                        });
                    }
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
                _logger.Error(ex, "Stripe - RefundPayment");
            }

            return ApiResult.Empty;
        }

        public override async Task<ApiResult> CancelPaymentAsync(PaymentProviderContext<StripeCheckoutSettings> ctx)
        {
            // NOTE: Subscriptions aren't currently abled to be "authorized" so the cancel
            // routine shouldn't be relevant for subscription payments at this point

            try
            {
                // See if there is a payment intent to cancel
                var stripePaymentIntentId = ctx.Order.Properties["stripePaymentIntentId"];
                if (!string.IsNullOrWhiteSpace(stripePaymentIntentId))
                {
                    var secretKey = ctx.Settings.TestMode ? ctx.Settings.TestSecretKey : ctx.Settings.LiveSecretKey;

                    ConfigureStripe(secretKey);

                    var paymentIntentService = new PaymentIntentService();
                    var intent = await paymentIntentService.CancelAsync(stripePaymentIntentId);

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
                var chargeId = ctx.Order.Properties["stripeChargeId"];
                if (chargeId != null)
                    return await RefundPaymentAsync(ctx);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Stripe - CancelPayment");
            }

            return ApiResult.Empty;
        }

        private bool IsRecurringOrderLine(OrderLineReadOnly orderLine)
        {
            return PropertyIsTrue(orderLine.Properties, Constants.Properties.Product.IsRecurringPropertyAlias);
        }

        private bool PropertyIsTrue(IReadOnlyDictionary<string, PropertyValue> props, string propAlias)
        {
            return props.ContainsKey(propAlias)
                && !string.IsNullOrWhiteSpace(props[propAlias])
                && (props[propAlias] == "1" || props[propAlias].Value.Equals("true", StringComparison.OrdinalIgnoreCase));
        }
    }
}
