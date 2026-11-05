namespace Hopover.App
{
    /// <summary>
    /// Every value that Paul still has to supply, in one place.
    /// TODO(Paul): replace each "[...]" value. A URL that still starts with "[" opens nothing; the click is logged.
    /// </summary>
    static class Branding
    {
        // TODO(Paul): the Stripe Payment Link for the coffee.
        public const string CoffeeUrl = "https://buy.stripe.com/28E4gz8pMasZgLv1Snbsc00";
        // TODO(Paul): the AlternativeTo page of Hopover, where the review goes.
        public const string ReviewUrl = "[REVIEW-URL]";
        // TODO(Paul): the public source repository.
        public const string SourceUrl = "https://github.com/madppiper/hopover";

        public const string PriceLabel = "pay what you want, €5 suggested";
        public const string LicenceName = "PolyForm Noncommercial 1.0.0";
        public const string ReviewSite = "AlternativeTo";
    }
}
