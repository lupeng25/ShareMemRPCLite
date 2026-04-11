using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ShareMemRPCLite
{
    internal sealed class UnitShareMem : IDisposable
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint ms);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ResetEvent(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetEvent(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenEvent(uint desiredAccess, bool inheritHandle, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        private static extern uint GetLastError();

        private const string ConnShareName = "GVision_Link_CShapeRPC";
        private const string ConnDoHandleName = "HANDLE_GVision_Link_DoJob";
        private const string ConnFinishHandleName = "HANDLE_GVision_Link_JobFinish";
        private const int ConnShareStrSize = 32768;

        public const string SplitKey = ";^";

        private readonly object runShareLock = new object();
        private MemoryMappedFile connMappedFile;
        private MemoryMappedViewAccessor connAccessor;
        private IntPtr connDoHandle = IntPtr.Zero;
        private IntPtr connFinishHandle = IntPtr.Zero;
        private Dictionary<int, SOneCamShareMem> runShareDic = new Dictionary<int, SOneCamShareMem>();
        private volatile bool isListenImage;
        private int needStartListenImageAndError;
        private bool disposed;

        public Func<int> LaunchGVisionAction { get; set; }

        public Func<bool> ReSendBitmapCamIndexFunc { get; set; }

        public bool HasBitmapCtl { get; set; }

        public int TempReceiveImg { get; set; } = -1;

        public string EXEPath { get; private set; }

        public DateTime LastWaitWriteOK { get; private set; } = DateTime.MinValue;

        public event EventHandler<ReceiveBitmapEventArgs> WhenReceiveBitmap;

        public UnitShareMem()
        {
            connMappedFile = MemoryMappedFile.CreateOrOpen(ConnShareName, Marshal.SizeOf(typeof(SConnShare)));
            connAccessor = connMappedFile.CreateViewAccessor();
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            StopListenImage();
            DisposeRunShares();

            if (connAccessor != null)
            {
                connAccessor.Dispose();
                connAccessor = null;
            }

            if (connMappedFile != null)
            {
                connMappedFile.Dispose();
                connMappedFile = null;
            }

            CloseConnHandles();
        }

        public GVisionRtnCode Init(out string exePath)
        {
            exePath = null;

            if (connMappedFile == null)
            {
                connMappedFile = MemoryMappedFile.CreateOrOpen(ConnShareName, Marshal.SizeOf(typeof(SConnShare)));
            }

            if (connAccessor == null)
            {
                connAccessor = connMappedFile.CreateViewAccessor();
            }

            if (connDoHandle == IntPtr.Zero)
            {
                connDoHandle = OpenEvent(0x0002, false, ConnDoHandleName);
                if (connDoHandle == IntPtr.Zero)
                {
                    return GVisionRtnCode.NoFindGVision;
                }
            }

            if (connFinishHandle == IntPtr.Zero)
            {
                connFinishHandle = OpenEvent(0x1F0003, false, ConnFinishHandleName);
                if (connFinishHandle == IntPtr.Zero)
                {
                    return GVisionRtnCode.NoFindGVision;
                }
            }

            string initMessage = string.Format("GetVersion{0}InitByRPC,needShow=true{0}GetExePath", SplitKey);
            WriteToShare(initMessage);
            GVisionRtnCode waitResult = WaitWriteOK(2000);
            if (waitResult != GVisionRtnCode.OK)
            {
                return GVisionRtnCode.NoFindGVision;
            }

            SConnShare shareValue;
            ReadShare(out shareValue);
            if (string.IsNullOrWhiteSpace(shareValue.ConnStr) || !shareValue.ConnStr.StartsWith("V", StringComparison.Ordinal))
            {
                return GVisionRtnCode.NoFindGVision;
            }

            string[] split1 = shareValue.ConnStr.Split(new[] { SplitKey }, StringSplitOptions.RemoveEmptyEntries);
            if (split1.Length < 3)
            {
                return GVisionRtnCode.VersionErr;
            }

            exePath = split1[2];
            EXEPath = exePath;

            string[] split2 = split1[1].Trim().Split(',');
            if (split2.Length % 3 != 0)
            {
                return GVisionRtnCode.DataLose;
            }

            return CreateRunShareMem(split2);
        }

        public bool IsGVisionExist()
        {
            IntPtr handle = OpenEvent(0x1F0003, false, "Global\\GVisionPreventRepeatOpening");
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            CloseHandle(handle);
            return true;
        }

        public GVisionRtnCode CheckAndInvokeGVision(int port = -1)
        {
            string exePath;
            GVisionRtnCode result = GVisionRtnCode.OK;
            int checkVersionPort = port == -1 ? 0 : port;

            if (Init(out exePath) != GVisionRtnCode.OK)
            {
                if (LaunchGVisionAction != null && LaunchGVisionAction() == 0)
                {
                    Thread.Sleep(1500);
                }

                result = Init(out exePath);
            }
            else if (!IsGVisionExist())
            {
                if (!IsGetVersionOK(checkVersionPort))
                {
                    DisposeRunShares();
                    CloseConnHandles();
                    if (LaunchGVisionAction != null && LaunchGVisionAction() == 0)
                    {
                        Thread.Sleep(1500);
                    }

                    result = RetryInit();
                }
                else
                {
                    result = GVisionRtnCode.FuncRtnTimeout;
                }
            }
            else if (!IsGetVersionOK(checkVersionPort))
            {
                DisposeRunShares();
                CloseConnHandles();
                result = RetryInit();
            }
            else
            {
                result = GVisionRtnCode.FuncRtnTimeout;
            }

            if (result == GVisionRtnCode.OK && ReSendBitmapCamIndexFunc != null)
            {
                ReSendBitmapCamIndexFunc();
            }

            return result;
        }

        public int WriteToShare(string value, int port = -1, bool canInvokeGVision = true)
        {
            MemoryMappedViewAccessor accessor;
            IntPtr doHandle;
            IntPtr finishHandle;
            int writeLength;
            long startPosition = 0;

            if (port >= 0 && runShareDic.ContainsKey(port))
            {
                accessor = runShareDic[port].RunAccessor;
                doHandle = runShareDic[port].RunDoHandle;
                finishHandle = runShareDic[port].RunFinishHandle;
                writeLength = runShareDic[port].StrSize;
            }
            else
            {
                accessor = connAccessor;
                doHandle = connDoHandle;
                finishHandle = connFinishHandle;
                writeLength = ConnShareStrSize;
                startPosition = 4;
            }

            if (accessor == null)
            {
                return -1;
            }

            byte[] bytes = new byte[writeLength];
            byte[] shortBytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            Array.Copy(shortBytes, bytes, Math.Min(shortBytes.Length, bytes.Length));
            accessor.WriteArray(startPosition, bytes, 0, bytes.Length);

            bool resetOk = ResetEvent(finishHandle);
            bool setOk = SetEvent(doHandle);
            if (resetOk && setOk)
            {
                return 0;
            }

            if (canInvokeGVision)
            {
                GVisionRtnCode invokeResult = CheckAndInvokeGVision(port);
                if (invokeResult == GVisionRtnCode.OK)
                {
                    return WriteToShare(value, port, false);
                }
            }

            return 4;
        }

        public GVisionRtnCode WaitWriteOK(uint timeMs = 200, int port = -1)
        {
            if (port >= 0 && !runShareDic.ContainsKey(port))
            {
                return GVisionRtnCode.NoFindGVision;
            }

            uint waitResult = port >= 0
                ? WaitForSingleObject(runShareDic[port].RunFinishHandle, timeMs)
                : WaitForSingleObject(connFinishHandle, timeMs);

            if (waitResult != 0)
            {
                uint error = GetLastError();
                if (error == 1008)
                {
                    return GVisionRtnCode.NoFindGVision;
                }
                return GVisionRtnCode.TimeOut;
            }

            LastWaitWriteOK = DateTime.Now;
            return GVisionRtnCode.OK;
        }

        public int ReadShare(out SConnShare value, int port = -1)
        {
            value = new SConnShare();

            MemoryMappedViewAccessor accessor;
            int size;
            long startPosition = 0;

            if (port >= 0 && runShareDic.ContainsKey(port))
            {
                accessor = runShareDic[port].RunAccessor;
                size = runShareDic[port].StrSize;
            }
            else
            {
                accessor = connAccessor;
                size = ConnShareStrSize;
                startPosition = 4;
            }

            if (accessor == null)
            {
                return -1;
            }

            value.HeatBeat = accessor.ReadInt32(0);
            byte[] bytes = new byte[size];
            accessor.ReadArray(startPosition, bytes, 0, bytes.Length);
            value.ConnStr = Encoding.UTF8.GetString(bytes).Trim('\0');
            return 0;
        }

        public int StartListenImage()
        {
            if (isListenImage)
            {
                return -2;
            }

            if (needStartListenImageAndError > 3)
            {
                return -9;
            }

            Dictionary<int, SOneCamShareMem> shareMap = GetRunShareDic();
            if (shareMap == null || shareMap.Count == 0)
            {
                needStartListenImageAndError++;
                return -1;
            }

            needStartListenImageAndError = 0;
            isListenImage = true;
            foreach (int key in shareMap.Keys)
            {
                int port = key;
                Thread imageThread = new Thread(() => ListenImageLoop(port))
                {
                    IsBackground = true,
                    Priority = ThreadPriority.AboveNormal,
                };
                imageThread.Start();
            }

            return 0;
        }

        public int CheckStartListenImage()
        {
            if (HasBitmapCtl && needStartListenImageAndError > 0 && !isListenImage)
            {
                return StartListenImage();
            }
            return 0;
        }

        public void StopListenImage()
        {
            isListenImage = false;
        }

        private GVisionRtnCode RetryInit()
        {
            string exePath;
            GVisionRtnCode result = GVisionRtnCode.NoDefine;
            for (int i = 0; i < 4; i++)
            {
                result = Init(out exePath);
                if (result == GVisionRtnCode.OK)
                {
                    break;
                }
                Thread.Sleep(200);
            }
            return result;
        }

        private bool IsGetVersionOK(int port = -1)
        {
            WriteToShare("GetVersion", port, false);
            GVisionRtnCode waitResult = WaitWriteOK(200, port);
            if (waitResult == GVisionRtnCode.OK)
            {
                return true;
            }

            SConnShare conn;
            ReadShare(out conn, port);
            return !string.IsNullOrWhiteSpace(conn.ConnStr) && conn.ConnStr.StartsWith("V", StringComparison.Ordinal);
        }

        private GVisionRtnCode CreateRunShareMem(string[] tokens)
        {
            DisposeRunShares();

            var tempCamMsg = new Dictionary<int, Tuple<int, int, int>>();
            var bindCamImage = new Dictionary<int, int>();

            for (int i = 0; i < tokens.Length / 3; i++)
            {
                ShareMemPrmType type;
                if (!Enum.TryParse(tokens[3 * i], true, out type))
                {
                    type = ShareMemPrmType.Null;
                }

                string order = tokens[3 * i + 1];
                int length;
                int.TryParse(tokens[3 * i + 2], out length);

                int cam = 0;
                if (type == ShareMemPrmType.Image)
                {
                    if (order.Contains(",") || order.Contains("'"))
                    {
                        string[] splitC = order.Split(new[] { ',', '\'' }, StringSplitOptions.RemoveEmptyEntries);
                        int mainCam;
                        if (int.TryParse(splitC[0], out mainCam))
                        {
                            cam = mainCam;
                            for (int j = 1; j < splitC.Length; j++)
                            {
                                int bindCam;
                                if (int.TryParse(splitC[j], out bindCam))
                                {
                                    bindCamImage[bindCam] = mainCam;
                                }
                            }
                        }
                    }
                    else
                    {
                        int.TryParse(order, out cam);
                    }
                }
                else if (type == ShareMemPrmType.String)
                {
                    int.TryParse(order, out cam);
                }

                if (!tempCamMsg.ContainsKey(cam))
                {
                    tempCamMsg[cam] = Tuple.Create(0, 0, 0);
                }

                Tuple<int, int, int> current = tempCamMsg[cam];
                if (type == ShareMemPrmType.Image)
                {
                    int imageBindCam = bindCamImage.ContainsKey(cam) ? bindCamImage[cam] : cam;
                    tempCamMsg[cam] = Tuple.Create(current.Item1, imageBindCam, length);
                }
                else if (type == ShareMemPrmType.String)
                {
                    tempCamMsg[cam] = Tuple.Create(length, current.Item2, current.Item3);
                }
            }

            lock (runShareLock)
            {
                foreach (KeyValuePair<int, Tuple<int, int, int>> item in tempCamMsg)
                {
                    int cam = item.Key;
                    int strLength = item.Value.Item1;
                    int imageBindCam = item.Value.Item2;
                    int imageSize = item.Value.Item3;

                    if (imageBindCam == cam)
                    {
                        runShareDic[cam] = new SOneCamShareMem(strLength, cam, imageSize);
                    }
                    else if (runShareDic.ContainsKey(imageBindCam))
                    {
                        SOneCamShareMem source = runShareDic[imageBindCam];
                        runShareDic[cam] = new SOneCamShareMem(strLength, cam, source.ImgSize, source.ImageMappedFile, source.ImageAccessor);
                    }
                    else
                    {
                        runShareDic[cam] = new SOneCamShareMem(strLength, cam, imageSize);
                    }
                }
            }

            return GVisionRtnCode.OK;
        }

        private Dictionary<int, SOneCamShareMem> GetRunShareDic()
        {
            lock (runShareLock)
            {
                return runShareDic.ToDictionary(pair => pair.Key, pair => pair.Value);
            }
        }

        private void DisposeRunShares()
        {
            lock (runShareLock)
            {
                foreach (SOneCamShareMem share in runShareDic.Values)
                {
                    share.Dispose();
                }
                runShareDic.Clear();
            }
        }

        private void CloseConnHandles()
        {
            if (connDoHandle != IntPtr.Zero)
            {
                CloseHandle(connDoHandle);
                connDoHandle = IntPtr.Zero;
            }

            if (connFinishHandle != IntPtr.Zero)
            {
                CloseHandle(connFinishHandle);
                connFinishHandle = IntPtr.Zero;
            }
        }

        private void ListenImageLoop(int port)
        {
            while (isListenImage)
            {
                if (!runShareDic.ContainsKey(port))
                {
                    Thread.Sleep(20);
                    continue;
                }

                SOneCamShareMem share = runShareDic[port];
                if (share.ImageFinishHandle == IntPtr.Zero || share.ImageAccessor == null)
                {
                    Thread.Sleep(20);
                    continue;
                }

                uint waitResult = WaitForSingleObject(share.ImageFinishHandle, 20);
                bool shouldRead = TempReceiveImg == port || (waitResult == 0 && HasBitmapCtl && WhenReceiveBitmap != null);
                if (!shouldRead)
                {
                    Thread.Sleep(5);
                    continue;
                }

                if (TempReceiveImg == port)
                {
                    TempReceiveImg = -1;
                }

                Bitmap bitmap = TryReadBitmap(share);
                if (bitmap != null)
                {
                    EventHandler<ReceiveBitmapEventArgs> handler = WhenReceiveBitmap;
                    if (handler != null)
                    {
                        handler(this, new ReceiveBitmapEventArgs(port, bitmap));
                    }
                }

                Thread.Sleep(5);
            }
        }

        private static Bitmap TryReadBitmap(SOneCamShareMem share)
        {
            int imgWH = share.ImageAccessor.ReadInt32(0);
            if (imgWH < 1)
            {
                return null;
            }

            int width = 1280;
            int height = 1024;
            int imageSize;
            if (imgWH > 10000000)
            {
                width = imgWH / 100000;
                height = imgWH % 100000;
                imageSize = width * height * 3;
            }
            else
            {
                imageSize = imgWH;
            }

            if (imageSize <= 0)
            {
                return null;
            }

            byte[] bytes = new byte[imageSize];
            share.ImageAccessor.ReadArray(4, bytes, 0, bytes.Length);
            return CreateBitmapFromBgr(bytes, width, height);
        }

        private static Bitmap CreateBitmapFromBgr(byte[] bytes, int width, int height)
        {
            Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            BitmapData bitmapData = bitmap.LockBits(new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

            int sourceStride = width * 3;
            try
            {
                for (int y = 0; y < height; y++)
                {
                    IntPtr targetRow = IntPtr.Add(bitmapData.Scan0, y * bitmapData.Stride);
                    Marshal.Copy(bytes, y * sourceStride, targetRow, sourceStride);
                }
            }
            finally
            {
                bitmap.UnlockBits(bitmapData);
            }

            return bitmap;
        }
    }
}
