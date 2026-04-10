using System;
using System.Collections.Generic;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ShareMemRPCLite
{
    [StructLayout(LayoutKind.Sequential)]
    public struct SharedFuncHeader
    {
        public int request_head;
        public int request_tail;
        public int response_head;
        public int response_tail;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct SharedFuncBlock
    {
        public long request_id;
        public int status;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 2048)]
        public string command;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 2048)]
        public string result;
    }

    public sealed class RpcFuncServer : IDisposable
    {
        public static RpcFuncServer Ins { get; set; }

        private const int BlockCount = 20;
        private const int HeaderSize = 16;
        private readonly Dictionary<string, FuncProcessorWithOut> funcDelegateDic = new Dictionary<string, FuncProcessorWithOut>();

        private MemoryMappedFile mmf;
        private MemoryMappedViewAccessor accessor;
        private EventWaitHandle requestEvent;
        private EventWaitHandle responseEvent;
        private SharedFuncHeader header;
        private CancellationTokenSource cts = new CancellationTokenSource();
        private bool disposed;

        public bool RegistFunc(FuncProcessorWithOut func, string startWith)
        {
            funcDelegateDic[startWith] = func;
            if (accessor == null)
            {
                bool isOk = Connect();
                if (isOk)
                {
                    Start();
                }
                return isOk;
            }
            return true;
        }

        public bool Connect()
        {
            try
            {
                int capacity = HeaderSize + BlockCount * Marshal.SizeOf(typeof(SharedFuncBlock));
                mmf = MemoryMappedFile.CreateOrOpen("RPC_FUNC_SHARED_MEM", capacity);
                if (mmf == null)
                {
                    return false;
                }

                accessor = mmf.CreateViewAccessor();
                if (accessor == null)
                {
                    return false;
                }

                ReadHeader();

                bool reqOk = EventWaitHandle.TryOpenExisting("RPC_FUNC_REQUEST_EVENT", out requestEvent);
                bool respOk = EventWaitHandle.TryOpenExisting("RPC_FUNC_RESPONSE_EVENT", out responseEvent);
                return reqOk && respOk;
            }
            catch
            {
                return false;
            }
        }

        public void Start()
        {
            if (!cts.IsCancellationRequested)
            {
                cts.Cancel();
            }

            cts = new CancellationTokenSource();
            Task.Run(() => ProcessRequests(cts.Token));
        }

        public void Stop()
        {
            if (!cts.IsCancellationRequested)
            {
                cts.Cancel();
            }

            if (requestEvent != null)
            {
                requestEvent.Set();
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

            if (accessor != null)
            {
                accessor.Dispose();
                accessor = null;
            }

            if (mmf != null)
            {
                mmf.Dispose();
                mmf = null;
            }

            if (requestEvent != null)
            {
                requestEvent.Dispose();
                requestEvent = null;
            }

            if (responseEvent != null)
            {
                responseEvent.Dispose();
                responseEvent = null;
            }
        }

        private void ReadHeader()
        {
            header.request_head = accessor.ReadInt32(0);
            header.request_tail = accessor.ReadInt32(4);
            header.response_head = accessor.ReadInt32(8);
            header.response_tail = accessor.ReadInt32(12);
        }

        private void WriteHeader(bool writeRequestHead, bool writeResponseTail)
        {
            if (writeRequestHead)
            {
                accessor.Write(0, header.request_head);
            }

            if (writeResponseTail)
            {
                accessor.Write(12, header.response_tail);
            }
        }

        private SharedFuncBlock ReadBlock(int index)
        {
            int blockSize = Marshal.SizeOf(typeof(SharedFuncBlock));
            long offset = HeaderSize + index * blockSize;

            SharedFuncBlock block = new SharedFuncBlock();
            block.request_id = accessor.ReadInt64(offset);
            block.status = accessor.ReadInt32(offset + 8);

            byte[] commandBytes = new byte[2048];
            accessor.ReadArray(offset + 12, commandBytes, 0, 2048);
            block.command = Encoding.UTF8.GetString(commandBytes).Trim('\0');
            return block;
        }

        private void WriteBlock(int index, SharedFuncBlock block)
        {
            int blockSize = Marshal.SizeOf(typeof(SharedFuncBlock));
            long offset = HeaderSize + index * blockSize;
            accessor.Write(offset, block.request_id);
            accessor.Write(offset + 8, block.status);

            byte[] resultBytes = new byte[2048];
            if (!string.IsNullOrWhiteSpace(block.result))
            {
                byte[] shortBytes = Encoding.UTF8.GetBytes(block.result);
                Array.Copy(shortBytes, resultBytes, Math.Min(shortBytes.Length, resultBytes.Length));
            }

            accessor.WriteArray(offset + 2060, resultBytes, 0, resultBytes.Length);
        }

        private void ProcessRequests(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (requestEvent == null || responseEvent == null)
                {
                    Thread.Sleep(100);
                    continue;
                }

                if (!requestEvent.WaitOne(200))
                {
                    continue;
                }

                ReadHeader();
                int repeatNum = 0;
                while (repeatNum < BlockCount + 2 && !token.IsCancellationRequested)
                {
                    repeatNum++;
                    int head = header.request_head;
                    int tail = header.request_tail;
                    if (head == tail)
                    {
                        break;
                    }

                    SharedFuncBlock block = ReadBlock(tail);
                    if (block.status != 1)
                    {
                        break;
                    }

                    long requestId = block.request_id;
                    string command = block.command;

                    header.request_head = (head + 1) % BlockCount;
                    WriteHeader(true, false);

                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        string result = string.Empty;
                        int resultCode = 0;

                        foreach (KeyValuePair<string, FuncProcessorWithOut> pair in funcDelegateDic)
                        {
                            if (command.StartsWith(pair.Key, StringComparison.Ordinal))
                            {
                                pair.Value(command, out result, out resultCode);
                                if (resultCode == 0)
                                {
                                    break;
                                }
                            }
                        }

                        if (resultCode != 0)
                        {
                            result = "__Error:" + resultCode + "__" + result;
                        }

                        int responseSlot = -1;
                        int tryCount = 0;
                        while (tryCount < 1000 && !token.IsCancellationRequested)
                        {
                            int responseTail = header.response_tail;
                            int nextTail = (responseTail + 1) % BlockCount;
                            if (nextTail == header.response_head)
                            {
                                Thread.Sleep(1);
                                tryCount++;
                                continue;
                            }

                            responseSlot = nextTail;
                            header.response_tail = responseSlot;
                            WriteHeader(false, true);
                            break;
                        }

                        if (responseSlot < 0)
                        {
                            return;
                        }

                        SharedFuncBlock responseBlock = new SharedFuncBlock
                        {
                            request_id = requestId,
                            status = 2,
                            result = result ?? string.Empty,
                        };

                        WriteBlock(responseSlot, responseBlock);
                        responseEvent.Set();
                    });
                }
            }
        }
    }
}
