using System.Linq;
using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Indexers.Shelfmark;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Validation.Paths;

namespace NzbDrone.Core.Download.Clients.Shelfmark
{
    public class ShelfmarkDownloadSettings : ShelfmarkSettings
    {
        [FieldDefinition(2, Label = "Download Folder", Type = FieldType.Path, HelpText = "Local staging folder for completed Shelfmark books. Must be writable by Chaptarr and outside your library.")]
        public string DownloadFolder { get; set; }

        [FieldDefinition(3, Label = "Skip Library Import", Type = FieldType.Checkbox, HelpText = "Let Shelfmark deliver books to Grimmory or another destination. Chaptarr monitors the download but does not retrieve files or mark the book as imported. Applies to new downloads.")]
        public bool SkipLibraryImport { get; set; }

        public override NzbDroneValidationResult Validate()
        {
            var validator = new InlineValidator<ShelfmarkDownloadSettings>();
            validator.RuleFor(c => c.DownloadFolder).IsValidPath().When(c => !c.SkipLibraryImport);
            return new NzbDroneValidationResult(base.Validate().Failures.Concat(validator.Validate(this).Errors));
        }
    }
}
