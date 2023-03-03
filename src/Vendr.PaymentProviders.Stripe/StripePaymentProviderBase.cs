using Newtonsoft.Json;
using Stripe;
using Stripe.Checkout;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Vendr.Common.Logging;
using Vendr.Core.Api;
using Vendr.Core.Models;
using Vendr.Core.PaymentProviders;

using StripeTaxRate = Stripe.TaxRate;

namespace Vendr.PaymentProviders.Stripe
{
    public abstract class StripePaymentProviderBase<TSelf, TSettings> : PaymentProviderBase<TSettings>
        where TSelf : StripePaymentProviderBase<TSelf, TSettings>
        where TSettings : StripeSettingsBase, new()
    {
        protected readonly ILogger<TSelf> _logger;

        public StripePaymentProviderBase(VendrContext vendr,
            ILogger<TSelf> logger)
            : base(vendr)
        {
            _logger = logger;
        }

        public override string GetCancelUrl(PaymentProviderContext<TSettings> ctx)
        {
            return ctx.Settings.CancelUrl;
        }

        public override string GetContinueUrl(PaymentProviderContext<TSettings> ctx)
        {
            return ctx.Settings.ContinueUrl; // + (settings.ContinueUrl.Contains("?") ? "&" : "?") + "session_id={CHECKOUT_SESSION_ID}";
        }

        public override string GetErrorUrl(PaymentProviderContext<TSettings> ctx)
        {
            return ctx.Settings.ErrorUrl;
        }

        public override async Task<OrderReference> GetOrderReferenceAsync(PaymentProviderContext<TSettings> ctx)
        {
            try
            {
                var secretKey = ctx.Settings.TestMode ? ctx.Settings.TestSecretKey : ctx.Settings.LiveSecretKey;
                var webhookSigningSecret = ctx.Settings.TestMode ? ctx.Settings.TestWebhookSigningSecret : ctx.Settings.LiveWebhookSigningSecret;

                ConfigureStripe(secretKey);

                var stripeEvent = await GetWebhookStripeEventAsync(ctx, webhookSigningSecret);
                if (stripeEvent != null && stripeEvent.Type == Events.CheckoutSessionCompleted)
                {
                    if (stripeEvent.Data?.Object?.Instance is Session stripeSession && !string.IsNullOrWhiteSpace(stripeSession.ClientReferenceId))
                    {
                        return OrderReference.Parse(stripeSession.ClientReferenceId);
                    }
                }
                else if (stripeEvent != null && stripeEvent.Type == Events.ReviewClosed)
                {
                    if (stripeEvent.Data?.Object?.Instance is Review stripeReview && !string.IsNullOrWhiteSpace(stripeReview.PaymentIntentId))
                    {
                        var paymentIntentService = new PaymentIntentService();
                        var paymentIntent = paymentIntentService.Get(stripeReview.PaymentIntentId);

                        if (paymentIntent != null && paymentIntent.Metadata.ContainsKey("orderReference"))
                        {
                            return OrderReference.Parse(paymentIntent.Metadata["orderReference"]);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Stripe - GetOrderReference");
            }

            return await base.GetOrderReferenceAsync(ctx);
        }

        protected StripeTaxRate GetOrCreateStripeTaxRate(PaymentProviderContext<TSettings> ctx, string taxName, decimal percentage, bool inclusive)
        {
            var taxRateService = new TaxRateService();
            var stripeTaxRates = new List<StripeTaxRate>();

            if (ctx.AdditionalData.ContainsKey("Vendr_StripeTaxRates"))
            {
                stripeTaxRates = (List<StripeTaxRate>)ctx.AdditionalData["Vendr_StripeTaxRates"];
            }

            if (stripeTaxRates.Count > 0)
            {
                var taxRate = GetStripeTaxRate(stripeTaxRates, taxName, percentage, inclusive);
                if (taxRate != null)
                    return taxRate;
            }

            stripeTaxRates = taxRateService.List(new TaxRateListOptions { Active = true }).ToList();

            if (ctx.AdditionalData.ContainsKey("Vendr_StripeTaxRates"))
            {
                ctx.AdditionalData["Vendr_StripeTaxRates"] = stripeTaxRates;
            }
            else
            {
                ctx.AdditionalData.Add("Vendr_StripeTaxRates", stripeTaxRates);
            }

            if (stripeTaxRates.Count > 0)
            {
                var taxRate = GetStripeTaxRate(stripeTaxRates, taxName, percentage, inclusive);
                if (taxRate != null)
                    return taxRate;
            }

            var newTaxRate = taxRateService.Create(new TaxRateCreateOptions
            {
                DisplayName = taxName,
                Percentage = percentage,
                Inclusive = inclusive,
            });

            stripeTaxRates.Add(newTaxRate);

            ctx.AdditionalData["Vendr_StripeTaxRates"] = stripeTaxRates;

            return newTaxRate;
        }

        private StripeTaxRate GetStripeTaxRate(IEnumerable<StripeTaxRate> taxRates, string taxName, decimal percentage, bool inclusive)
        {
            return taxRates.FirstOrDefault(x => x.Percentage == percentage && x.Inclusive == inclusive && x.DisplayName == taxName);
        }

        protected async Task<StripeWebhookEvent> GetWebhookStripeEventAsync(PaymentProviderContext<TSettings> ctx, string webhookSigningSecret)
        {
            StripeWebhookEvent stripeEvent = null;

            if (ctx.AdditionalData.ContainsKey("Vendr_StripeEvent"))
            {
                stripeEvent = (StripeWebhookEvent)ctx.AdditionalData["Vendr_StripeEvent"];
            }
            else
            {
                try
                {
                    var stream = await ctx.Request.Content.ReadAsStreamAsync();

                    if (stream.CanSeek)
                    {
                        stream.Seek(0, SeekOrigin.Begin);
                    }

                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        var json = await reader.ReadToEndAsync();
                        var stripeSignature = ctx.Request.Headers.GetValues("Stripe-Signature").FirstOrDefault();

                        // Just validate the webhook signature
                        EventUtility.ValidateSignature(json, stripeSignature, webhookSigningSecret);

                        // Parse the event ourselves to our custom webhook event model
                        // as it only captures minimal object information.
                        stripeEvent = JsonConvert.DeserializeObject<StripeWebhookEvent>(json);

                        // We manually fetch the event object type ourself as it means it will be fetched
                        // using the same API version as the payment providers is coded against.
                        // NB: Only supports a number of object types we are likely to be interested in.
                        if (stripeEvent?.Data?.Object != null)
                        {
                            switch (stripeEvent.Data.Object.Type)
                            {
                                case "checkout.session":
                                    var sessionService = new SessionService();
                                    stripeEvent.Data.Object.Instance = await sessionService.GetAsync(stripeEvent.Data.Object.Id);
                                    break;
                                case "charge":
                                    var chargeService = new ChargeService();
                                    stripeEvent.Data.Object.Instance = await chargeService.GetAsync(stripeEvent.Data.Object.Id);
                                    break;
                                case "payment_intent":
                                    var paymentIntentService = new PaymentIntentService();
                                    stripeEvent.Data.Object.Instance = await paymentIntentService.GetAsync(stripeEvent.Data.Object.Id);
                                    break;
                                case "subscription":
                                    var subscriptionService = new SubscriptionService();
                                    stripeEvent.Data.Object.Instance = await subscriptionService.GetAsync(stripeEvent.Data.Object.Id);
                                    break;
                                case "invoice":
                                    var invoiceService = new InvoiceService();
                                    stripeEvent.Data.Object.Instance = await invoiceService.GetAsync(stripeEvent.Data.Object.Id);
                                    break;
                                case "review":
                                    var reviewService = new ReviewService();
                                    stripeEvent.Data.Object.Instance = await reviewService.GetAsync(stripeEvent.Data.Object.Id);
                                    break;
                            }
                        }

                        ctx.AdditionalData.Add("Vendr_StripeEvent", stripeEvent);

                    }
                    
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Stripe - GetWebhookStripeEvent");
                }
            }

            return stripeEvent;
        }

        protected static void ConfigureStripe(string apiKey)
        {
            StripeConfiguration.ApiKey = apiKey;
            StripeConfiguration.MaxNetworkRetries = 2;
        }

        protected string GetTransactionId(PaymentIntent paymentIntent)
        {
            return GetTransactionId(paymentIntent.LatestCharge);
        }

        protected string GetTransactionId(Invoice invoice)
        {
            return GetTransactionId(invoice.Charge);
        }

        protected string GetTransactionId(Charge charge)
        {
            return charge?.Id;
        }

        protected PaymentStatus GetPaymentStatus(Invoice invoice)
        {
            // Possible Invoice statuses:
            // - draft
            // - open
            // - paid
            // - void
            // - uncollectible

            if (invoice.Status == "void")
                return PaymentStatus.Cancelled;

            if (invoice.Status == "open")
                return PaymentStatus.Authorized;

            if (invoice.Status == "paid")
            {
                if (invoice.PaymentIntent != null)
                    return GetPaymentStatus(invoice.PaymentIntent);

                if (invoice.Charge != null)
                    return GetPaymentStatus(invoice.Charge);

                return PaymentStatus.Captured;
            }

            if (invoice.Status == "uncollectible")
                return PaymentStatus.Error;

            return PaymentStatus.Initialized;
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

            // Need this to occur before authorize / succeeded checks
            if (paymentIntent.Review != null && paymentIntent.Review.Open)
                return PaymentStatus.PendingExternalSystem;

            if (paymentIntent.Status == "requires_capture")
                return PaymentStatus.Authorized;

            if (paymentIntent.Status == "succeeded")
            {
                if (paymentIntent.LatestCharge != null)
                {
                    return GetPaymentStatus(paymentIntent.LatestCharge);
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

                if (charge.Captured)
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
