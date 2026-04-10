using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace ShareMemRPCLite
{
    public sealed class GrabImageSucceededEventArgs : EventArgs
    {
        public byte[] ImageBytes { get; private set; }

        public int CamID { get; private set; }

        public GrabImageSucceededEventArgs(byte[] imageBytes)
            : this(imageBytes, -1)
        {
        }

        public GrabImageSucceededEventArgs(byte[] imageBytes, int camId)
        {
            ImageBytes = imageBytes ?? new byte[0];
            CamID = camId;
        }
    }

    public interface IFrontendVisionService
    {
        event EventHandler<GrabImageSucceededEventArgs> GrabImageSucceeded;

        void NotifyGrabImageSucceeded(byte[] imageBytes);
    }

    /// <summary>
    /// Frontend-facing vision service.
    /// Converts GVisionQt bitmap events to byte[] events for UI/web layers.
    /// </summary>
    public sealed class FrontendVisionService : IFrontendVisionService, IDisposable
    {
        private readonly CallGVision gv;
        private readonly ImageCodecInfo jpegCodec;
        private readonly long jpegQuality;
        private bool disposed;

        public FrontendVisionService(CallGVision gv, long jpegQuality = 90L)
        {
            if (gv == null)
            {
                throw new ArgumentNullException("gv");
            }

            this.gv = gv;
            this.jpegCodec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
            this.jpegQuality = Math.Max(1L, Math.Min(100L, jpegQuality));
            this.gv.WhenReceiveBitmap += Gv_WhenReceiveBitmap;
        }

        public event EventHandler<GrabImageSucceededEventArgs> GrabImageSucceeded;

        public void NotifyGrabImageSucceeded(byte[] imageBytes)
        {
            NotifyGrabImageSucceeded(imageBytes, -1);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            gv.WhenReceiveBitmap -= Gv_WhenReceiveBitmap;
        }

        private void NotifyGrabImageSucceeded(byte[] imageBytes, int camId)
        {
            EventHandler<GrabImageSucceededEventArgs> handler = GrabImageSucceeded;
            if (handler != null)
            {
                handler(this, new GrabImageSucceededEventArgs(imageBytes, camId));
            }
        }

        private void Gv_WhenReceiveBitmap(object sender, ReceiveBitmapEventArgs e)
        {
            if (e == null || e.Image == null)
            {
                return;
            }

            byte[] imageBytes = BitmapToJpegBytes(e.Image);
            NotifyGrabImageSucceeded(imageBytes, e.CamID);
        }

        private byte[] BitmapToJpegBytes(Bitmap bitmap)
        {
            using (var ms = new MemoryStream())
            {
                if (jpegCodec != null)
                {
                    using (var encoderParams = new EncoderParameters(1))
                    {
                        encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, jpegQuality);
                        bitmap.Save(ms, jpegCodec, encoderParams);
                    }
                }
                else
                {
                    bitmap.Save(ms, ImageFormat.Jpeg);
                }

                return ms.ToArray();
            }
        }
    }
}
