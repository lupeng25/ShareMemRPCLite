# GKG.ShareMemRPCLite

`GKG.ShareMemRPCLite` 是从 `RPC.CShape` 中拆分出的轻量级 C# 通信库，专注于和 `GVisionQt` 的共享内存通信。

## 能力范围

- 启动/重连 `GVisionQt`
- 发送指令并等待结果（`RunAndWaitRst`）
- 订阅图像帧（`WhenReceiveBitmap` / `ImageReceived`）
- 查询 Tab 信息（`GetAllTabs`）
- 注册共享内存 RPC 回调函数（`RPC_FUNC_SHARED_MEM`）

## 快速开始（CallGVision）

```csharp
using ShareMemRPCLite;

using (var gv = new CallGVision(isInvokeGVision: true))
{
    gv.WhenReceiveBitmap += (s, e) =>
    {
        try
        {
            // e.Image: System.Drawing.Bitmap
            // 在这里处理图像
        }
        finally
        {
            e.Image?.Dispose();
        }
    };

    gv.SetReceiveBitmapCamIndex(0, true);
    gv.CheckStartListenImage();

    SGVisionRtn result;
    var code = gv.RunAndWaitRst("FindModel", 0, out result, Tuple.Create("score", "0.8"));
}
```

## 推荐：简化实时订阅（GVisionRealtimeBitmapClient）

如果 UI 端不想自己管理 `CallGVision` 的重连/补订阅流程，可以使用新增封装类 `GVisionRealtimeBitmapClient`。

它会自动处理：

- 启动后定时补订阅
- `GVisionQt` 后启动场景的自动恢复
- 事件解绑与资源释放

```csharp
using ShareMemRPCLite;

public sealed class RealtimePage : IDisposable
{
    private readonly GVisionRealtimeBitmapClient client;

    public RealtimePage()
    {
        client = new GVisionRealtimeBitmapClient(camId: 0, isInvokeGVision: true, ensureIntervalMs: 2000);
        client.BitmapReceived += OnBitmapReceived;
        client.Error += OnRealtimeError;
        client.Start();
    }

    private void OnBitmapReceived(object? sender, ReceiveBitmapEventArgs e)
    {
        try
        {
            // e.Image: System.Drawing.Bitmap
            // 在这里显示或处理图像
        }
        finally
        {
            // 有订阅者时，释放责任在订阅者
            e.Image?.Dispose();
        }
    }

    private static void OnRealtimeError(object? sender, RealtimeClientErrorEventArgs e)
    {
        Console.WriteLine(e.Message);
    }

    public void Dispose()
    {
        client.BitmapReceived -= OnBitmapReceived;
        client.Error -= OnRealtimeError;
        client.Dispose();
    }
}
```

## Window3 最小接入示例（Avalonia）

下面是和当前 `Window3` 一致的最小接入思路：页面只关心“订阅帧 + 显示图像”，连接与补订阅由 `GVisionRealtimeBitmapClient` 负责。

```csharp
using Avalonia;
using AvaloniaVisionControl;
using ShareMemRPCLite;

public sealed partial class Window3 : Window
{
    private readonly GVisionRealtimeBitmapClient client = new GVisionRealtimeBitmapClient(
        camId: 0,
        isInvokeGVision: true,
        ensureIntervalMs: 2000);

    public Window3()
    {
        InitializeComponent();
        client.BitmapReceived += OnBitmapReceived;
        client.Error += (s, e) => Console.WriteLine(e.Message);
        client.Start();
    }

    private void OnBitmapReceived(object? sender, ReceiveBitmapEventArgs e)
    {
        try
        {
            // 1) 将 e.Image(System.Drawing.Bitmap) 转为 Avalonia Bitmap
            // 2) 调用 CtlOnlyShowImage.ShowImage(...) 显示
        }
        finally
        {
            e.Image?.Dispose();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        client.BitmapReceived -= OnBitmapReceived;
        client.Dispose();
        base.OnClosed(e);
    }
}
```

说明：

- 若 `Window3` 先启动、`GVisionQt` 后启动，客户端会自动补订阅。
- 若需要显示到 `CtlOnlyShowImage`，仍需做 `System.Drawing.Bitmap -> Avalonia.Bitmap` 转换。

## 前端字节流模式（FrontendVisionService）

`FrontendVisionService` 适合需要 `byte[]` 图像数据的前端场景（例如 WebView 或网络传输）。

- 输入：`CallGVision.WhenReceiveBitmap` 的 `Bitmap`
- 输出：`GrabImageSucceeded` 的 JPEG `byte[]`

```csharp
using ShareMemRPCLite;

using var gv = new CallGVision(isInvokeGVision: true);
using var frontend = new FrontendVisionService(gv);

frontend.GrabImageSucceeded += (s, e) =>
{
    byte[] imageBytes = e.ImageBytes;
    int camId = e.CamID;
};

gv.SetReceiveBitmapCamIndex(0, true);
gv.CheckStartListenImage();
```

事件链路：

`GVisionQt 图像 -> CallGVision.WhenReceiveBitmap -> FrontendVisionService(Bitmap 转 JPEG byte[]) -> GrabImageSucceeded`

## 资源释放说明

- `WhenReceiveBitmap` / `BitmapReceived` 回调中的 `e.Image` 是 `System.Drawing.Bitmap`。
- 当你订阅了事件并消费图像时，请在回调里 `Dispose()`，避免内存泄漏。
- `GVisionRealtimeBitmapClient` 在“无订阅者”情况下会自动释放收到的图像。

## 构建

```powershell
dotnet build ShareMemRPCLite\GKG.ShareMemRPCLite.csproj -c Release
```
