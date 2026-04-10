using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace ShareMemRPCLite
{
    public sealed class CallGVision : IDisposable
    {
        public const string Version = "V0.1.0 [Lite]";
        private readonly Dictionary<string, int> camAliasIdDic = new Dictionary<string, int>();
        private readonly Dictionary<EventHandler<ImageReceivedEventArgs>, EventHandler<ReceiveBitmapEventArgs>> imageEventBridgeMap =
            new Dictionary<EventHandler<ImageReceivedEventArgs>, EventHandler<ReceiveBitmapEventArgs>>();
        private readonly object imageEventLock = new object();
        private int receiveBitmapEventNum;
        private bool disposed;

        public CallGVision(bool isInvokeGVision = true)
        {
            if (isInvokeGVision)
            {
                ShareMemBaseMgr.Ins.ShareMem.IsGVisionExist();
            }

            RpcFuncServer.Ins = new RpcFuncServer();
        }

        public uint TimeoutMs { get; set; } = 3000;

        public uint LinkErrRepeatNum { get; set; } = 1;

        public RpcFuncServer m_FuncServer
        {
            get { return RpcFuncServer.Ins; }
        }

        public string GVisionExePath
        {
            get { return ShareMemBaseMgr.Ins.GVisinoEXEPath ?? ShareMemBaseMgr.Ins.ShareMem.EXEPath; }
        }

        public bool IsGVisionExist
        {
            get { return ShareMemBaseMgr.Ins.ShareMem.IsGVisionExist(); }
        }

        public event EventHandler<ReceiveBitmapEventArgs> WhenReceiveBitmap
        {
            add
            {
                ShareMemBaseMgr.Ins.ShareMem.HasBitmapCtl = true;
                ShareMemBaseMgr.Ins.ShareMem.WhenReceiveBitmap += value;
                receiveBitmapEventNum++;
                if (receiveBitmapEventNum == 1)
                {
                    ShareMemBaseMgr.Ins.ShareMem.StartListenImage();
                }
            }
            remove
            {
                ShareMemBaseMgr.Ins.ShareMem.WhenReceiveBitmap -= value;
                receiveBitmapEventNum = Math.Max(0, receiveBitmapEventNum - 1);
                if (receiveBitmapEventNum == 0)
                {
                    ShareMemBaseMgr.Ins.ShareMem.HasBitmapCtl = false;
                    ShareMemBaseMgr.Ins.ShareMem.StopListenImage();
                }
            }
        }

        // New UI-friendly image event: subscribers receive Bitmap directly in args.
        public event EventHandler<ImageReceivedEventArgs> ImageReceived
        {
            add
            {
                if (value == null)
                {
                    return;
                }

                EventHandler<ReceiveBitmapEventArgs> bridge = (sender, args) =>
                {
                    value(sender, new ImageReceivedEventArgs(args.Image, args.CamID));
                };

                lock (imageEventLock)
                {
                    imageEventBridgeMap[value] = bridge;
                }

                WhenReceiveBitmap += bridge;
            }
            remove
            {
                if (value == null)
                {
                    return;
                }

                EventHandler<ReceiveBitmapEventArgs> bridge = null;
                lock (imageEventLock)
                {
                    if (imageEventBridgeMap.ContainsKey(value))
                    {
                        bridge = imageEventBridgeMap[value];
                        imageEventBridgeMap.Remove(value);
                    }
                }

                if (bridge != null)
                {
                    WhenReceiveBitmap -= bridge;
                }
            }
        }

        public event EventHandler<EventArgs> WhenInvokeGVision
        {
            add { ShareMemBaseMgr.Ins.WhenInvokeGVision += value; }
            remove { ShareMemBaseMgr.Ins.WhenInvokeGVision -= value; }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (RpcFuncServer.Ins != null)
            {
                RpcFuncServer.Ins.Dispose();
                RpcFuncServer.Ins = null;
            }
        }

        public bool RegistRpcFunc(FuncProcessorWithOut func, string startWith)
        {
            return m_FuncServer.RegistFunc(func, startWith);
        }

        public void ReceiveShowImageOnce(int camId)
        {
            ShareMemBaseMgr.Ins.ShareMem.TempReceiveImg = camId;
        }

        public bool SetReceiveBitmapCamIndex(int camId, bool isAllowReceive)
        {
            return ShareMemBaseMgr.Ins.SetReceiveBitmapCamIndex(camId, isAllowReceive);
        }

        public bool UpdateBitmapCamIndex()
        {
            return ShareMemBaseMgr.Ins.ReSendBitmapCamIndex();
        }

        public bool SetCameraAlias(int camId, string name)
        {
            camAliasIdDic[name] = camId;
            return true;
        }

        public int CheckStartListenImage()
        {
            return ShareMemBaseMgr.Ins.ShareMem.CheckStartListenImage();
        }

        public GVisionRtnCode CheckAndInvokeGVision()
        {
            return ShareMemBaseMgr.Ins.ShareMem.CheckAndInvokeGVision(-1);
        }

        public GVisionRtnCode RunAndWaitRst(string tabName, int portOrCam, out SGVisionRtn cvRst,
            params Tuple<string, string>[] prms)
        {
            ShareMemBaseMgr.Ins.ShareMem.CheckStartListenImage();
            GVisionRtnCode ret = RunNoWait(tabName, portOrCam, prms);
            ret = WaitRst(portOrCam, out cvRst, TimeoutMs);

            int repeatNum = 0;
            while (ret != GVisionRtnCode.OK)
            {
                if (ret == GVisionRtnCode.FuncRtnTimeout && repeatNum >= LinkErrRepeatNum && portOrCam != -1)
                {
                    GVisionRtnCode invokeRet = ShareMemBaseMgr.Ins.ShareMem.CheckAndInvokeGVision(-1);
                    if (invokeRet == GVisionRtnCode.OK)
                    {
                        Thread.Sleep(100);
                        ret = RunNoWait(tabName, portOrCam, prms);
                        ret = WaitRst(portOrCam, out cvRst, TimeoutMs);
                    }
                }

                if (ret == GVisionRtnCode.OK || repeatNum >= LinkErrRepeatNum)
                {
                    break;
                }

                GVisionRtnCode invokePortRet = ShareMemBaseMgr.Ins.ShareMem.CheckAndInvokeGVision(portOrCam);
                if (invokePortRet != GVisionRtnCode.FuncRtnTimeout)
                {
                    Thread.Sleep(100);
                    ret = RunNoWait(tabName, portOrCam, prms);
                }

                ret = WaitRst(portOrCam, out cvRst, TimeoutMs);
                repeatNum++;
                if (invokePortRet == GVisionRtnCode.FuncRtnTimeout)
                {
                    if (ret == GVisionRtnCode.OK)
                    {
                        break;
                    }
                    ret = invokePortRet;
                }
            }

            return ret;
        }

        public GVisionRtnCode RunAndWaitRst(string tabName, int portOrCam, out string cvRstStr,
            params Tuple<string, string>[] prms)
        {
            ShareMemBaseMgr.Ins.ShareMem.CheckStartListenImage();
            GVisionRtnCode ret = RunNoWait(tabName, portOrCam, prms);
            ret = WaitRst(portOrCam, out cvRstStr, TimeoutMs);

            int repeatNum = 0;
            while (ret != GVisionRtnCode.OK)
            {
                if (ret == GVisionRtnCode.FuncRtnTimeout && repeatNum >= LinkErrRepeatNum && portOrCam != -1)
                {
                    GVisionRtnCode invokeRet = ShareMemBaseMgr.Ins.ShareMem.CheckAndInvokeGVision(-1);
                    if (invokeRet == GVisionRtnCode.OK)
                    {
                        Thread.Sleep(100);
                        ret = RunNoWait(tabName, portOrCam, prms);
                        ret = WaitRst(portOrCam, out cvRstStr, TimeoutMs);
                    }
                }

                if (ret == GVisionRtnCode.OK || repeatNum >= LinkErrRepeatNum)
                {
                    break;
                }

                GVisionRtnCode invokePortRet = ShareMemBaseMgr.Ins.ShareMem.CheckAndInvokeGVision(portOrCam);
                if (invokePortRet != GVisionRtnCode.FuncRtnTimeout)
                {
                    Thread.Sleep(100);
                    ret = RunNoWait(tabName, portOrCam, prms);
                }

                ret = WaitRst(portOrCam, out cvRstStr, TimeoutMs);
                repeatNum++;
                if (invokePortRet == GVisionRtnCode.FuncRtnTimeout)
                {
                    if (ret == GVisionRtnCode.OK)
                    {
                        break;
                    }
                    ret = invokePortRet;
                }
            }

            return ret;
        }

        public GVisionRtnCode RunAndWaitRst(string tabName, string camAlias, out SGVisionRtn cvRst,
            params Tuple<string, string>[] prms)
        {
            if (camAliasIdDic.ContainsKey(camAlias))
            {
                return RunAndWaitRst(tabName, camAliasIdDic[camAlias], out cvRst, prms);
            }

            cvRst = new SGVisionRtn(true);
            return GVisionRtnCode.NoDefine;
        }

        public GVisionRtnCode RunAndWaitRst(string tabName, string camAlias, out string cvRstStr,
            params Tuple<string, string>[] prms)
        {
            if (camAliasIdDic.ContainsKey(camAlias))
            {
                return RunAndWaitRst(tabName, camAliasIdDic[camAlias], out cvRstStr, prms);
            }

            cvRstStr = string.Empty;
            return GVisionRtnCode.NoDefine;
        }

        public GVisionRtnCode RunNoWait(string tabName, int portOrCam, params Tuple<string, string>[] prms)
        {
            var builder = new StringBuilder();
            builder.Append(tabName);
            foreach (Tuple<string, string> param in prms ?? Enumerable.Empty<Tuple<string, string>>())
            {
                builder.AppendFormat(",{0}={1}", param.Item1, param.Item2);
            }

            int ret = ShareMemBaseMgr.Ins.ShareMem.WriteToShare(builder.ToString(), portOrCam);
            return ret == 0 ? GVisionRtnCode.OK : GVisionRtnCode.TimeOut;
        }

        public GVisionRtnCode WaitRst(int portOrCam, out SGVisionRtn cvRst, uint waitTime)
        {
            string resultText;
            GVisionRtnCode ret = WaitRst(portOrCam, out resultText, waitTime);
            if (ret != GVisionRtnCode.OK)
            {
                cvRst = new SGVisionRtn(true);
                return ret;
            }

            if (string.IsNullOrWhiteSpace(resultText))
            {
                cvRst = new SGVisionRtn(true);
                return GVisionRtnCode.DataLose;
            }

            try
            {
                cvRst = JsonConvert.DeserializeObject<SGVisionRtn>(resultText);
            }
            catch
            {
                cvRst = new SGVisionRtn(true);
                cvRst.ErrorMsg = resultText;
                cvRst.StringDic["Error"] = resultText;
            }

            return ret;
        }

        public GVisionRtnCode WaitRst(int portOrCam, out string cvRstStr, uint waitTime)
        {
            cvRstStr = string.Empty;
            GVisionRtnCode waitRet = ShareMemBaseMgr.Ins.ShareMem.WaitWriteOK(waitTime, portOrCam);
            if (waitRet != GVisionRtnCode.OK)
            {
                return waitRet;
            }

            SConnShare value;
            ShareMemBaseMgr.Ins.ShareMem.ReadShare(out value, portOrCam);
            cvRstStr = value.ConnStr;
            return GVisionRtnCode.OK;
        }

        public GVisionRtnCode SendOrder(string order, out string rtn, uint timeoutMs = 2000, int spanTime = 0, int port = -1)
        {
            rtn = string.Empty;
            ShareMemBaseMgr.Ins.ShareMem.WriteToShare(order, port);
            if (spanTime > 0)
            {
                Thread.Sleep(spanTime);
            }

            GVisionRtnCode waitRet = ShareMemBaseMgr.Ins.ShareMem.WaitWriteOK(timeoutMs, port);
            if (waitRet != GVisionRtnCode.OK)
            {
                return waitRet;
            }

            SConnShare value;
            ShareMemBaseMgr.Ins.ShareMem.ReadShare(out value, port);
            rtn = value.ConnStr;
            return GVisionRtnCode.OK;
        }

        public GVisionRtnCode GetGVisionInfo(out IList<STabInfo> infos)
        {
            infos = new List<STabInfo>();
            string jsonText;
            GVisionRtnCode ret = SendOrder("GetAllTabs", out jsonText, 1200, 50);
            if (ret != GVisionRtnCode.OK || string.IsNullOrWhiteSpace(jsonText))
            {
                return ret;
            }

            if (string.IsNullOrWhiteSpace(ShareMemBaseMgr.Ins.GVisinoEXEPath) &&
                !string.IsNullOrWhiteSpace(ShareMemBaseMgr.Ins.ShareMem.EXEPath))
            {
                ShareMemBaseMgr.Ins.GVisinoEXEPath = ShareMemBaseMgr.Ins.ShareMem.EXEPath;
            }

            try
            {
                infos = JsonConvert.DeserializeObject<List<STabInfo>>(jsonText);
            }
            catch
            {
                return GVisionRtnCode.NoDefine;
            }

            return GVisionRtnCode.OK;
        }
    }
}
