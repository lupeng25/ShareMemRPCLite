using System;
using System.Collections.Generic;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace ShareMemRPCLite
{
    [StructLayout(LayoutKind.Sequential)]
    public struct SConnShare
    {
        public int HeatBeat;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32768)]
        public string ConnStr;
    }

    public enum GVisionRtnCode
    {
        OK = 0,
        NoDefine = 1,
        TimeOut = 2,
        ScriptErr = 3,
        NoFindGVision = 4,
        VersionErr,
        DataLose,
        FuncRtnTimeout,
    }

    internal enum ShareMemPrmType
    {
        Null = 0,
        Int,
        Double,
        Bool,
        String,
        Image,
        HANDLE,
        Other = 100,
    }

    internal sealed class SOneCamShareMem : IDisposable
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenEvent(uint desiredAccess, bool inheritHandle, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        private const string RunShareName = "GVision_Run_CShapeRPC";
        private const string ImageShareName = "Local\\GVision_Image_CShapeRPC";
        private const string RunDoHandleName = "HANDLE_GVision_Run_DoJob";
        private const string RunFinishHandleName = "HANDLE_GVision_Run_JobFinish";
        private const string ImageFinishHandleName = "HANDLE_GVision_ShowImage_JobFinish";

        public int CamID { get; private set; }

        public MemoryMappedFile RunMappedFile { get; private set; }

        public MemoryMappedViewAccessor RunAccessor { get; private set; }

        public MemoryMappedFile ImageMappedFile { get; private set; }

        public MemoryMappedViewAccessor ImageAccessor { get; private set; }

        public IntPtr RunDoHandle { get; private set; }

        public IntPtr RunFinishHandle { get; private set; }

        public IntPtr ImageFinishHandle { get; private set; }

        public int StrSize { get; private set; }

        public int ImgSize { get; private set; }

        public SOneCamShareMem(int strSize, int camId, int imageSize = -1,
            MemoryMappedFile imageMap = null, MemoryMappedViewAccessor imageAccessor = null)
        {
            CamID = camId;
            StrSize = strSize;
            ImgSize = imageSize;

            RunMappedFile = MemoryMappedFile.CreateOrOpen(RunShareName + camId, strSize);
            RunAccessor = RunMappedFile.CreateViewAccessor();

            if (imageMap == null && imageSize > 0)
            {
                ImageMappedFile = MemoryMappedFile.CreateOrOpen(ImageShareName + camId, imageSize);
                ImageAccessor = ImageMappedFile.CreateViewAccessor();
            }
            else
            {
                ImageMappedFile = imageMap;
                ImageAccessor = imageAccessor;
            }

            RunDoHandle = OpenEvent(0x0002, false, RunDoHandleName + camId);
            RunFinishHandle = OpenEvent(0x1F0003, false, RunFinishHandleName + camId);
            ImageFinishHandle = OpenEvent(0x1F0003, false, ImageFinishHandleName + camId);
        }

        public void Dispose()
        {
            if (RunAccessor != null)
            {
                RunAccessor.Dispose();
                RunAccessor = null;
            }

            if (RunMappedFile != null)
            {
                RunMappedFile.Dispose();
                RunMappedFile = null;
            }

            if (ImageAccessor != null)
            {
                ImageAccessor.Dispose();
                ImageAccessor = null;
            }

            if (ImageMappedFile != null)
            {
                ImageMappedFile.Dispose();
                ImageMappedFile = null;
            }

            CloseHandleSafe(RunDoHandle);
            CloseHandleSafe(RunFinishHandle);
            CloseHandleSafe(ImageFinishHandle);
        }

        private static void CloseHandleSafe(IntPtr handle)
        {
            if (handle != IntPtr.Zero)
            {
                CloseHandle(handle);
            }
        }
    }

    public enum TabInnerPrmType
    {
        Other = 0,
        Int,
        Double,
        Bool,
        String,
        Enum,
        FullPath,
        DirPath,
    }

    public struct STabInnerSinglePrm
    {
        public string Name;
        public string ShowName;

        [JsonConverter(typeof(StringEnumConverter))]
        public TabInnerPrmType Type;

        public string DefValue;
        public IList<string> EnumValues;
        public double MinValue;
        public double MaxValue;
        public double Interval;
    }

    public struct STabInfo
    {
        public string TabName;
        public string TabShowName;
        public string TabInfo;
        public IList<STabInnerSinglePrm> Params;
    }

    public struct SGVisionRtn
    {
        public double TimeMs;
        public string RunTabName;
        public short Port;
        public string ErrorMsg;
        public int FindNum;
        public double X;
        public double Y;
        public IDictionary<string, int> IntDic;
        public IDictionary<string, double> DoubleDic;
        public IDictionary<string, bool> BoolDic;
        public IDictionary<string, string> StringDic;

        public SGVisionRtn(bool init)
        {
            TimeMs = 0;
            RunTabName = null;
            Port = -1;
            ErrorMsg = null;
            FindNum = 0;
            X = 0;
            Y = 0;
            IntDic = new Dictionary<string, int>();
            DoubleDic = new Dictionary<string, double>();
            BoolDic = new Dictionary<string, bool>();
            StringDic = new Dictionary<string, string>();
        }
    }

    public delegate void FuncProcessorWithOut(string input, out string outStr, out int outInt);
}
