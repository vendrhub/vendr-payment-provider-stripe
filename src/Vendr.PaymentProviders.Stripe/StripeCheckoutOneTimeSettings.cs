using Vendr.Core.Web.PaymentProviders;

namespace Vendr.PaymentProviders.Stripe
{
    public class StripeCheckoutOneTimeSettings : StripeSettingsBase
    {
        [PaymentProviderSetting(Name = "Capture", 
            Description = "Flag indicating whether to immediately capture the payment, or whether to just authorize the payment for later (manual) capture.",
            SortOrder = 1000)]
        public bool Capture { get; set; }

        [PaymentProviderSetting(Name = "Send Stripe Receipt", 
            Description = "Flag indicating whether to send a Stripe receipt to the customer. Receipts are only sent when in live mode.",
            SortOrder = 1100)]
        public bool SendStripeReceipt { get; set; }
    }
}
