using Newtonsoft.Json;
using Stripe;
using Stripe.Checkout;
using System;
using System.IO;
using System.Web;
using Vendr.Core;
using Vendr.Core.Models;
using Vendr.Core.Web.Api;
using Vendr.Core.Web.PaymentProviders;

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
            return settings.ContinueUrl + (settings.ContinueUrl.Contains("?") ? "&" : "?") + "session_id={CHECKOUT_SESSION_ID}";
        }

        public override string GetErrorUrl(OrderReadOnly order, TSettings settings)
        {
            return settings.ErrorUrl;
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
                                case "session":
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

        protected static long DollarsToCents(decimal val)
        {
            var cents = val * 100M;
            var centsRounded = Math.Round(cents, MidpointRounding.AwayFromZero);
            return Convert.ToInt64(centsRounded);
        }

        protected static decimal CentsToDollars(long val)
        {
            return val / 100M;
        }
    }
}
