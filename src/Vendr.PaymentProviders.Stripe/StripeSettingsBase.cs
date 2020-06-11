using Vendr.Core.Web.PaymentProviders;

namespace Vendr.PaymentProviders.Stripe
{
    public class StripeSettingsBase
    {
        [PaymentProviderSetting(Name = "Continue URL", 
            Description = "The URL to continue to after this provider has done processing. eg: /continue/",
            SortOrder = 100)]
        public string ContinueUrl { get; set; }

        [PaymentProviderSetting(Name = "Cancel URL", 
            Description = "The URL to return to if the payment attempt is canceled. eg: /cancel/",
            SortOrder = 200)]
        public string CancelUrl { get; set; }

        [PaymentProviderSetting(Name = "Error URL",
            Description = "The URL to return to if the payment attempt errors. eg: /error/",
            SortOrder = 300)]
        public string ErrorUrl { get; set; }

        [PaymentProviderSetting(Name = "Billing Address (Line 1) Property Alias",
            Description = "The order property alias containing line 1 of the billing address",
            SortOrder = 400)]
        public string BillingAddressLine1PropertyAlias { get; set; }

        [PaymentProviderSetting(Name = "Billing Address (Line 2) Property Alias",
            Description = "The order property alias containing line 2 of the billing address",
            SortOrder = 500)]
        public string BillingAddressLine2PropertyAlias { get; set; }

        [PaymentProviderSetting(Name = "Billing Address City Property Alias",
            Description = "The order property alias containing the city of the billing address",
            SortOrder = 600)]
        public string BillingAddressCityPropertyAlias { get; set; }

        [PaymentProviderSetting(Name = "Billing Address State Property Alias",
            Description = "The order property alias containing the state of the billing address",
            SortOrder = 700)]
        public string BillingAddressStatePropertyAlias { get; set; }

        [PaymentProviderSetting(Name = "Billing Address ZipCode Property Alias",
            Description = "The order property alias containing the zip code of the billing address",
            SortOrder = 800)]
        public string BillingAddressZipCodePropertyAlias { get; set; }

        [PaymentProviderSetting(Name = "Test Secret Key", 
            Description = "Your test Stripe secret key",
            SortOrder = 900)]
        public string TestSecretKey { get; set; }
        
        [PaymentProviderSetting(Name = "Test Public Key", 
            Description = "Your test Stripe public key",
            SortOrder = 1000)]
        public string TestPublicKey { get; set; }

        [PaymentProviderSetting(Name = "Test Webhook Signing Secret",
            Description = "Your test Stripe webhook signing secret",
            SortOrder = 1100)]
        public string TestWebhookSigningSecret { get; set; }

        [PaymentProviderSetting(Name = "Live Secret Key", 
            Description = "Your live Stripe secret key",
            SortOrder = 1200)]
        public string LiveSecretKey { get; set; }

        [PaymentProviderSetting(Name = "Live Public Key", 
            Description = "Your live Stripe public key",
            SortOrder = 1300)]
        public string LivePublicKey { get; set; }

        [PaymentProviderSetting(Name = "Live Webhook Signing Secret",
            Description = "Your live Stripe webhook signing secret",
            SortOrder = 1400)]
        public string LiveWebhookSigningSecret { get; set; }

        [PaymentProviderSetting(Name = "Test Mode",
            Description = "Set whether to process payments in test mode.",
            SortOrder = 10000)]
        public bool TestMode { get; set; }

        // Advanced settings

        [PaymentProviderSetting(Name = "Order Heading",
            Description = "A heading to display on the order summary of the Stripe Checkout screen.",
            IsAdvanced = true,
            SortOrder = 1000100)]
        public string OrderHeading { get; set; }

        [PaymentProviderSetting(Name = "Order Image",
            Description = "The URL of an image to display on the order summary of the Stripe Checkout screen. Should be 480x480px.",
            IsAdvanced = true,
            SortOrder = 1000200)]
        public string OrderImage { get; set; }
    }
}
