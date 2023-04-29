﻿using Vendr.Core.PaymentProviders;

namespace Vendr.PaymentProviders.Stripe
{
    public class StripeCheckoutSettings : StripeSettingsBase
    {
        [PaymentProviderSetting(Name = "Capture", 
            Description = "Flag indicating whether to immediately capture the payment, or whether to just authorize the payment for later (manual) capture. Only supported when the payment is a non-subscription based payment. Subscription based payments will always be captured immediately.",
            SortOrder = 2000)]
        public bool Capture { get; set; }

        [PaymentProviderSetting(Name = "Send Stripe Receipt", 
            Description = "Flag indicating whether to send a Stripe receipt to the customer. Receipts are only sent when in live mode.",
            SortOrder = 2100)]
        public bool SendStripeReceipt { get; set; }

        // Advanced settings

        [PaymentProviderSetting(Name = "One-Time Items Heading",
            Description = "A heading to display for the total one-time payment items order line when the order consists of both subscription and one-time payment items",
            IsAdvanced = true,
            SortOrder = 1000210)]
        public string OneTimeItemsHeading { get; set; }

        [PaymentProviderSetting(Name = "Order Properties",
            Description = "A comma separated list of order properties to copy to the transactions meta data",
            IsAdvanced = true,
            SortOrder = 1000300)]
        public string OrderProperties { get; set; }

        [PaymentProviderSetting(Name = "Payment Method Types",
            Description = "A comma separated list of Stripe payment method types to use. Defaults to just 'card' if left empty.",
            IsAdvanced = true,
            SortOrder = 1000400)]
        public string PaymentMethodTypes { get; set; }

        [PaymentProviderSetting(Name = "Disable Automatic Tax Calculation",
            Description = "Flag indicating whether Stripe should automatically calculate tax on a checkout session. Ensure 'Enable automatic tax calculation' is disabled from the stripe dashboard settings.",
            IsAdvanced = true,
            SortOrder = 1000500)]
        public bool DisableAutomaticTax { get; set; }


        [PaymentProviderSetting(Name = "Disable Subscription Tax",
            Description = "Flag indicating whether to disable the 'Subscription Tax' from being added by default to recurring subscription products based on the order line tax rate.",
            IsAdvanced = true,
            SortOrder = 1000600)]
        public bool DisableSubscriptionTax { get; set; }
    }
}
