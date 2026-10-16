using System;
using System.IO;
using ImageMagick;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;

namespace NzbDrone.Core.MediaCover
{
    public interface IImageResizer
    {
        void Resize(string source, string destination, int height);
    }

    public class ImageResizer : IImageResizer
    {
        private readonly IDiskProvider _diskProvider;

        public ImageResizer(IDiskProvider diskProvider, IPlatformInfo platformInfo)
        {
            _diskProvider = diskProvider;
        }

        public void Resize(string source, string destination, int height)
        {
            if (height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(height));
            }

            try
            {
                var format = string.Equals(Path.GetExtension(destination), ".jfif", StringComparison.OrdinalIgnoreCase)
                    ? MagickFormat.Jpeg
                    : (MagickFormatInfo.Create(destination)
                        ?? throw new NotSupportedException($"Unsupported cover image extension: {destination}")).Format;
                using var image = new MagickImage(source);
                image.Resize(0, (uint)height);
                image.Quality = 95;
                image.Settings.SetDefine(MagickFormat.Jpeg, "sampling-factor", "4:4:4");
                image.Write(destination, format);
            }
            catch
            {
                if (_diskProvider.FileExists(destination))
                {
                    _diskProvider.DeleteFile(destination);
                }

                throw;
            }
        }
    }
}
