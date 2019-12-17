using Stripe;
using System;
using System.IO;
using System.Web;
using Vendr.Core;
using Vendr.Core.Models;
using Vendr.Core.Web.Api;
using Vendr.Core.Web.PaymentProviders;

namespace Vendr.PaymentProvider.Stripe
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

        protected Event GetWebhookStripeEvent(HttpRequestBase request, string webhookSigningSecret)
        {
            Event stripeEvent = null;

            if (HttpContext.Current.Items["Vendr_StripeEvent"] != null)
            {
                stripeEvent = (Event)HttpContext.Current.Items["Vendr_StripeEvent"];
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

                        stripeEvent = EventUtility.ConstructEvent(json, request.Headers["Stripe-Signature"], webhookSigningSecret, throwOnApiVersionMismatch: false);

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
