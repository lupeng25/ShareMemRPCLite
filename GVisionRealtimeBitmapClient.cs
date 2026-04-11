using System;
using System.Collections.Generic;
using System.Threading;

namespace ShareMemRPCLite
{
    public sealed class RealtimeClientErrorEventArgs : EventArgs
    {
        public string Message { get; private set; }

        public Exception Exception { get; private set; }

        public RealtimeClientErrorEventArgs(string message, Exception exception)
        {
            Message = message ?? string.Empty;
            Exception = exception;
        }
    }

    /// <summary>
    /// Wraps CallGVision image subscription lifecycle for a single camera.
    /// Handles invoke/re-subscribe/re-listen automatically on a timer.
    /// </summary>
    public sealed class GVisionRealtimeBitmapClient : IDisposable
    {
        private readonly object syncRoot = new object();
        private readonly CallGVision gVision;
        private readonly int camId;
        private readonly int ensureIntervalMs;
        private readonly bool subscribeAllCams;
        private readonly int subscribeMaxCamId;
        private readonly Timer ensureTimer;
        private readonly HashSet<int> subscribedCamIds = new HashSet<int>();
        private int ensureRunning;
        private bool started;
        private bool disposed;

        public GVisionRealtimeBitmapClient(
            int camId = 0,
            bool isInvokeGVision = true,
            int ensureIntervalMs = 2000,
            bool subscribeAllCams = false,
            int subscribeMaxCamId = 15)
        {
            this.camId = camId;
            this.ensureIntervalMs = Math.Max(200, ensureIntervalMs);
            this.subscribeAllCams = subscribeAllCams;
            this.subscribeMaxCamId = Math.Max(0, subscribeMaxCamId);
            gVision = new CallGVision(isInvokeGVision);
            gVision.WhenInvokeGVision += GVision_WhenInvokeGVision;
            gVision.WhenReceiveBitmap += GVision_WhenReceiveBitmap;
            ensureTimer = new Timer(EnsureTimerTick, null, Timeout.Infinite, Timeout.Infinite);
        }

        public int CamId
        {
            get { return camId; }
        }

        public event EventHandler<ReceiveBitmapEventArgs> BitmapReceived;

        public event EventHandler<RealtimeClientErrorEventArgs> Error;

        public void Start()
        {
            ThrowIfDisposed();

            lock (syncRoot)
            {
                if (started)
                {
                    return;
                }

                started = true;
            }

            ensureTimer.Change(0, ensureIntervalMs);
            ScheduleEnsure();
        }

        public void Stop()
        {
            lock (syncRoot)
            {
                if (!started)
                {
                    return;
                }

                started = false;
            }

            ensureTimer.Change(Timeout.Infinite, Timeout.Infinite);

            try
            {
                if (subscribeAllCams)
                {
                    lock (syncRoot)
                    {
                        foreach (int subscribedCamId in subscribedCamIds)
                        {
                            gVision.SetReceiveBitmapCamIndex(subscribedCamId, false);
                        }

                        subscribedCamIds.Clear();
                    }
                }
                else
                {
                    gVision.SetReceiveBitmapCamIndex(camId, false);
                }
            }
            catch (Exception ex)
            {
                OnError(string.Format("Realtime unsubscribe failed: {0}", ex.Message), ex);
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            Stop();

            gVision.WhenInvokeGVision -= GVision_WhenInvokeGVision;
            gVision.WhenReceiveBitmap -= GVision_WhenReceiveBitmap;

            ensureTimer.Dispose();
            gVision.Dispose();
        }

        private void EnsureTimerTick(object state)
        {
            ScheduleEnsure();
        }

        private void GVision_WhenInvokeGVision(object sender, EventArgs e)
        {
            ScheduleEnsure();
        }

        private void ScheduleEnsure()
        {
            if (disposed || !started)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref ensureRunning, 1, 0) != 0)
            {
                return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    EnsureSubscription();
                }
                finally
                {
                    Interlocked.Exchange(ref ensureRunning, 0);
                }
            });
        }

        private void EnsureSubscription()
        {
            if (disposed || !started)
            {
                return;
            }

            try
            {
                gVision.CheckAndInvokeGVision();
                EnsureCameraSubscriptions();

                try
                {
                    gVision.CheckStartListenImage();
                    gVision.UpdateBitmapCamIndex();
                }
                catch (Exception ex)
                {
                    OnError(string.Format("Refresh Cam{0} failed: {1}", camId, ex.Message), ex);
                }
            }
            catch (Exception ex)
            {
                OnError(string.Format("Ensure realtime stream failed: {0}", ex.Message), ex);
            }
        }

        private void EnsureCameraSubscriptions()
        {
            if (subscribeAllCams)
            {
                for (int id = 0; id <= subscribeMaxCamId; id++)
                {
                    EnsureSingleCamSubscription(id);
                }
                return;
            }

            EnsureSingleCamSubscription(camId);
        }

        private void EnsureSingleCamSubscription(int targetCamId)
        {
            lock (syncRoot)
            {
                if (subscribedCamIds.Contains(targetCamId))
                {
                    return;
                }
            }

            try
            {
                if (gVision.SetReceiveBitmapCamIndex(targetCamId, true))
                {
                    lock (syncRoot)
                    {
                        subscribedCamIds.Add(targetCamId);
                    }
                }
            }
            catch (Exception ex)
            {
                OnError(string.Format("Subscribe Cam{0} failed: {1}", targetCamId, ex.Message), ex);
            }
        }

        private void GVision_WhenReceiveBitmap(object sender, ReceiveBitmapEventArgs e)
        {
            if (e == null || e.Image == null)
            {
                return;
            }

            if (!subscribeAllCams && e.CamID != camId)
            {
                e.Image.Dispose();
                return;
            }

            EventHandler<ReceiveBitmapEventArgs> handler = BitmapReceived;
            if (handler != null)
            {
                handler(this, e);
                return;
            }

            e.Image.Dispose();
        }

        private void OnError(string message, Exception ex)
        {
            EventHandler<RealtimeClientErrorEventArgs> handler = Error;
            if (handler != null)
            {
                handler(this, new RealtimeClientErrorEventArgs(message, ex));
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException("GVisionRealtimeBitmapClient");
            }
        }
    }
}
