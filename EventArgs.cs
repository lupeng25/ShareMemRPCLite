using System;
using System.Drawing;

namespace ShareMemRPCLite
{
    public sealed class ImageReceivedEventArgs : EventArgs
    {
        public int CamID { get; private set; }

        public Bitmap Image { get; private set; }

        public ImageReceivedEventArgs(Bitmap image, int camId)
        {
            Image = image;
            CamID = camId;
        }
    }

    public sealed class ReceiveBitmapEventArgs : EventArgs
    {
        public int CamID { get; private set; }

        public Bitmap Image { get; private set; }

        public ReceiveBitmapEventArgs(int camId, Bitmap image)
        {
            CamID = camId;
            Image = image;
        }
    }
}
