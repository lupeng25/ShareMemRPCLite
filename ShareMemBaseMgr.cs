using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace ShareMemRPCLite
{
    internal sealed class ShareMemBaseMgr : IDisposable
    {
        public static readonly string EXEFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "gvisionPath.txt");
        private static readonly Lazy<ShareMemBaseMgr> lazy = new Lazy<ShareMemBaseMgr>(() => new ShareMemBaseMgr());

        private readonly HashSet<int> receiveBitmapCamIndexes = new HashSet<int>();
        private DateTime lastLaunchGVision = DateTime.MinValue;
        private int repeatOpenNgNum;
        private bool disposed;

        public static ShareMemBaseMgr Ins
        {
            get { return lazy.Value; }
        }

        public UnitShareMem ShareMem { get; private set; }

        public string GVisinoEXEPath { get; set; }

        public event EventHandler<EventArgs> WhenInvokeGVision;

        private ShareMemBaseMgr()
        {
            if (File.Exists(EXEFilePath))
            {
                try
                {
                    GVisinoEXEPath = File.ReadAllText(EXEFilePath);
                }
                catch
                {
                }
            }

            ShareMem = new UnitShareMem();
            ShareMem.LaunchGVisionAction = LaunchGVision;
            ShareMem.ReSendBitmapCamIndexFunc = ReSendBitmapCamIndex;

            try
            {
                string exePath;
                GVisionRtnCode result = ShareMem.Init(out exePath);
                if (result != GVisionRtnCode.OK && LaunchGVision() == 0)
                {
                    Thread.Sleep(1000);
                    result = ShareMem.Init(out exePath);
                }

                if (!string.IsNullOrWhiteSpace(exePath) && !string.Equals(GVisinoEXEPath, exePath, StringComparison.OrdinalIgnoreCase))
                {
                    SaveExePath(exePath);
                }
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (ShareMem != null)
            {
                ShareMem.Dispose();
                ShareMem = null;
            }
        }

        public bool SetReceiveBitmapCamIndex(int camId, bool allowReceive)
        {
            if (allowReceive)
            {
                receiveBitmapCamIndexes.Add(camId);
            }
            else if (receiveBitmapCamIndexes.Contains(camId))
            {
                receiveBitmapCamIndexes.Remove(camId);
            }
            else
            {
                return false;
            }

            string command = string.Format("UpdateShowImgCtl^^{0}^^{1}", allowReceive ? "add" : "remove", camId);
            ShareMem.WriteToShare(command, -1, false);
            return ShareMem.WaitWriteOK(400) == GVisionRtnCode.OK;
        }

        public bool ReSendBitmapCamIndex()
        {
            if (receiveBitmapCamIndexes.Count == 0)
            {
                return false;
            }

            List<string> commands = receiveBitmapCamIndexes
                .OrderBy(item => item)
                .Select(item => string.Format("UpdateShowImgCtl^^add^^{0}", item))
                .ToList();

            ShareMem.WriteToShare(string.Join(UnitShareMem.SplitKey, commands), -1, false);
            return ShareMem.WaitWriteOK(400) == GVisionRtnCode.OK;
        }

        public int LaunchGVision()
        {
            if (DateTime.Now - lastLaunchGVision < TimeSpan.FromSeconds(15) || repeatOpenNgNum > 3)
            {
                Thread.Sleep(2000);
                return 0;
            }

            string[] processNames = { "GVision", "GVisionQt" };
            bool alreadyRunning = processNames.Any(name => Process.GetProcessesByName(name).Length > 0);
            if (alreadyRunning || ShareMem.IsGVisionExist())
            {
                return 0;
            }

            foreach (string path in GetCandidatePaths())
            {
                try
                {
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    Process.Start(path);
                    if (WaitLaunchGVisionOK())
                    {
                        lastLaunchGVision = DateTime.Now;
                        SaveExePath(path);
                    }
                    return 0;
                }
                catch
                {
                }
            }

            repeatOpenNgNum++;
            return 2;
        }

        private bool WaitLaunchGVisionOK()
        {
            for (int i = 0; i < 40; i++)
            {
                Thread.Sleep(50);
                if (ShareMem.IsGVisionExist())
                {
                    EventHandler<EventArgs> handler = WhenInvokeGVision;
                    if (handler != null)
                    {
                        handler(this, EventArgs.Empty);
                    }
                    return true;
                }
            }
            return false;
        }

        private IEnumerable<string> GetCandidatePaths()
        {
            var paths = new List<string>();
            if (!string.IsNullOrWhiteSpace(GVisinoEXEPath))
            {
                paths.Add(GVisinoEXEPath);
            }

            string env = Environment.GetEnvironmentVariable("GKGVISIONROOT");
            if (!string.IsNullOrWhiteSpace(env))
            {
                foreach (string item in env.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    paths.Add(Path.Combine(item, "GVision.exe"));
                    paths.Add(Path.Combine(item, "GVisionQt.exe"));
                }
            }

            string assemblyPath = Assembly.GetExecutingAssembly().Location;
            string assemblyDir = Path.GetDirectoryName(assemblyPath);
            if (!string.IsNullOrWhiteSpace(assemblyDir))
            {
                paths.Add(Path.Combine(assemblyDir, "GVision.exe"));
                paths.Add(Path.Combine(assemblyDir, "GVisionQt.exe"));
                paths.Add(Path.Combine(assemblyDir, "GVision", "GVision.exe"));
                paths.Add(Path.Combine(assemblyDir, "GVision", "GVisionQt.exe"));

                DirectoryInfo parent = Directory.GetParent(assemblyDir);
                if (parent != null)
                {
                    paths.Add(Path.Combine(parent.FullName, "GVision.exe"));
                    paths.Add(Path.Combine(parent.FullName, "GVisionQt.exe"));
                }
            }

            return paths.Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private void SaveExePath(string exePath)
        {
            GVisinoEXEPath = exePath;
            try
            {
                File.WriteAllText(EXEFilePath, exePath);
            }
            catch
            {
            }
        }
    }
}
