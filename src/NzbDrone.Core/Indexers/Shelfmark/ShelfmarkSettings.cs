using System;
using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Indexers.Shelfmark
{
    public class ShelfmarkSettingsValidator : AbstractValidator<ShelfmarkSettings>
    {
        public ShelfmarkSettingsValidator()
        {
            RuleFor(c => c.BaseUrl).ValidRootUrl()
                .Must(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                    string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment))
                .WithMessage("Use the Shelfmark base URL without credentials, query parameters or fragments");
        }
    }

    public class ShelfmarkSettings : IProviderConfig
    {
        [FieldDefinition(0, Label = "URL", HelpText = "Shelfmark base URL, including any reverse proxy path")]
        public string BaseUrl { get; set; } = "http://localhost:8084";

        [FieldDefinition(1, Label = "API Key", Type = FieldType.Password, Privacy = PrivacyLevel.ApiKey, HelpText = "SHELFMARK_API_KEY (admin key). Leave empty only when Shelfmark authentication is disabled.")]
        public string ApiKey { get; set; }

        public virtual NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(new ShelfmarkSettingsValidator().Validate(this));
        }
    }

    public class ShelfmarkIndexerSettings : ShelfmarkSettings, IIndexerSettings
    {
        [FieldDefinition(2, Label = "Early Download Limit", Type = FieldType.Number, Unit = "days", Advanced = true)]
        public int? EarlyReleaseLimit { get; set; }
    }
}
