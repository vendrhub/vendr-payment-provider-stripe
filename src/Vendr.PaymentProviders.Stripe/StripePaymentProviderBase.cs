using Newtonsoft.Json;
using Stripe;
using Stripe.Checkout;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web;
using Vendr.Core;
using Vendr.Core.Models;
using Vendr.Core.Web.Api;
using Vendr.Core.Web.PaymentProviders;

using StripeTaxRate = Stripe.TaxRate;

namespace Vendr.PaymentProviders.Stripe
{
    public abstract class StripePaymentProviderBase<TSettings> : PaymentProviderBase<TSettings>
        where TSettings : StripeSettingsBase, new()
    {
        public StripePaymentProviderBase(VendrContext vendr)
            : base(vendr)
        { }

        public override string GetCancelUrl(OrderReadOnly order, TSettings settings)
        {
            return settings.CancelUrl;
        }

        public override string GetContinueUrl(OrderReadOnly order, TSettings settings)
        {
            return settings.ContinueUrl; // + (settings.ContinueUrl.Contains("?") ? "&" : "?") + "session_id={CHECKOUT_SESSION_ID}";
        }

        public override string GetErrorUrl(OrderReadOnly order, TSettings settings)
        {
            return settings.ErrorUrl;
        }

        public override OrderReference GetOrderReference(HttpRequestBase request, TSettings settings)
        {
            try
            {
                var secretKey = settings.TestMode ? settings.TestSecretKey : settings.LiveSecretKey;
                var webhookSigningSecret = settings.TestMode ? settings.TestWebhookSigningSecret : settings.LiveWebhookSigningSecret;

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

        protected StripeTaxRate GetOrCreateStripeTaxRate(string taxName, decimal percentage, bool inclusive)
        {
            var taxRateService = new TaxRateService();
            var stripeTaxRates = new List<StripeTaxRate>();

            if (HttpContext.Current.Items["Vendr_StripeTaxRates"] != null)
            {
                stripeTaxRates = (List<StripeTaxRate>)HttpContext.Current.Items["Vendr_StripeTaxRates"];
            }

            if (stripeTaxRates.Count > 0)
            {
                var taxRate = GetStripeTaxRate(stripeTaxRates, taxName, percentage, inclusive);
                if (taxRate != null)
                    return taxRate;
            }

            HttpContext.Current.Items["Vendr_StripeTaxRates"] = stripeTaxRates = taxRateService.List(new TaxRateListOptions
            {
                Active = true
            }).ToList();

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

            HttpContext.Current.Items["Vendr_StripeTaxRates"] = stripeTaxRates;

            return newTaxRate;
        }

        private StripeTaxRate GetStripeTaxRate(IEnumerable<StripeTaxRate> taxRates, string taxName, decimal percentage, bool inclusive)
        {
            return taxRates.FirstOrDefault(x => x.Percentage == percentage && x.Inclusive == inclusive && x.DisplayName == taxName);
        }

        protected StripeWebhookEvent GetWebhookStripeEvent(HttpRequestBase request, string webhookSigningSecret)
        {
            StripeWebhookEvent stripeEvent = null;

            if (HttpContext.Current.Items["Vendr_StripeEvent"] != null)
            {
                stripeEvent = (StripeWebhookEvent)HttpContext.Current.Items["Vendr_StripeEvent"];
            }
            else
            {
                try
                {
                    if (request.InputStream.CanSeek)
                        request.InputStream.Seek(0, SeekOrigin.Begin);

                    using (var reader = new StreamReader(request.InputStream))
                    {
                        var json = reader.ReadToEnd();

                        // Just validate the webhook signature
                        EventUtility.ValidateSignature(json, request.Headers["Stripe-Signature"], webhookSigningSecret);

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
                                    stripeEvent.Data.Object.Instance = sessionService.Get(stripeEvent.Data.Object.Id);
                                    break;
                                case "charge":
                                    var chargeService = new ChargeService();
                                    stripeEvent.Data.Object.Instance = chargeService.Get(stripeEvent.Data.Object.Id);
                                    break;
                                case "payment_intent": 
                                    var paymentIntentService = new PaymentIntentService();
                                    stripeEvent.Data.Object.Instance = paymentIntentService.Get(stripeEvent.Data.Object.Id);
                                    break;
                                case "subscription":
                                    var subscriptionService = new SubscriptionService();
                                    stripeEvent.Data.Object.Instance = subscriptionService.Get(stripeEvent.Data.Object.Id);
                                    break;
                                case "invoice":
                                    var invoiceService = new InvoiceService();
                                    stripeEvent.Data.Object.Instance = invoiceService.Get(stripeEvent.Data.Object.Id);
                                    break;
                            }
                        }

                        HttpContext.Current.Items["Vendr_StripeEvent"] = stripeEvent;
                    }
                }
                catch (Exception ex)
                {
                    Vendr.Log.Error<StripePaymentProviderBase<TSettings>>(ex, "Stripe - GetWebhookStripeEvent");
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
            return (paymentIntent.Charges?.Data?.Count ?? 0) > 0
                ? GetTransactionId(paymentIntent.Charges.Data[0])
                : null;
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
