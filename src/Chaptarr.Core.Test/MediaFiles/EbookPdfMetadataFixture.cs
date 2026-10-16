using System;
using System.IO;
using System.IO.Abstractions;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using PdfSharp.Pdf;

namespace Chaptarr.Core.Test.MediaFiles
{
    [TestFixture]
    public class EbookPdfMetadataFixture
    {
        [Test]
        public void should_extract_pdf_metadata_without_modifying_the_source()
        {
            var path = Path.Combine(Path.GetTempPath(), "chaptarr-pdf-" + Guid.NewGuid().ToString("N") + ".pdf");
            try
            {
                using (var document = new PdfDocument())
                {
                    document.AddPage();
                    document.Info.Title = "Dune";
                    document.Info.Author = "Frank Herbert";
                    document.Info.Subject = "Science fiction";
                    document.Info.Keywords = "desert; spice";
                    document.Info.Creator = "Metadata fixture";
                    document.Info.CreationDate = new DateTime(2026, 1, 2, 12, 0, 0);
                    document.Save(path);
                }

                var original = File.ReadAllBytes(path);
                var subject = new EBookTagService(null, null, null, null, null, null, LogManager.GetCurrentClassLogger());
                var tags = subject.ReadAllTags(new FileSystem().FileInfo.FromFileName(path));

                Assert.Multiple(() =>
                {
                    Assert.That(tags["title"], Is.EqualTo(new[] { "Dune" }));
                    Assert.That(tags["author"], Is.EqualTo(new[] { "Frank Herbert" }));
                    Assert.That(tags["authors"], Is.EqualTo(tags["author"]));
                    Assert.That(tags["subject"], Is.EqualTo(new[] { "Science fiction" }));
                    Assert.That(tags["description"], Is.EqualTo(tags["subject"]));
                    Assert.That(tags["keywords"], Is.EqualTo(new[] { "desert; spice" }));
                    Assert.That(tags["tags"], Is.EqualTo(tags["keywords"]));
                    Assert.That(tags["creator"], Is.EqualTo(new[] { "Metadata fixture" }));
                    Assert.That(tags["creation_date"], Is.EqualTo(new[] { "2026-01-02" }));
                    Assert.That(File.ReadAllBytes(path), Is.EqualTo(original));
                });
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
