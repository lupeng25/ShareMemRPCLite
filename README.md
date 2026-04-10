# GKG.ShareMemRPCLite

`GKG.ShareMemRPCLite` 是从 `RPC.CShape` 中独立出的轻量级 C# 通信库。

它只保留 `RPC.CShape` 与 `GVisionQt` 的通信能力：

- 启动和重连 `GVisionQt`
- 共享内存指令通道
- 执行 Tab 并等待返回
- 接收 `GVisionQt` 图像
- 注册 RPC 回调函数（`RPC_FUNC_SHARED_MEM`）
- 查询 Tab 信息（`GetAllTabs`）

它不包含 UI 控件和示例应用。

相关文档：

- 迁移说明：`MIGRATION.md`

## 快速开始

引用 `GKG.ShareMemRPCLite.dll`，通过 `ShareMemRPCLite.CallGVision` 作为入口。

```csharp
using ShareMemRPCLite;

using (var gv = new CallGVision())
{
    gv.ImageReceived += (s, e) =>
    {
        // e.Image 是从 GVisionQt 收到的 Bitmap。
        // 如果要更新 WinForms/WPF 控件，请先切回 UI 线程。
    };

    gv.SetReceiveBitmapCamIndex(0, true);

    SGVisionRtn result;
    var code = gv.RunAndWaitRst("FindModel", 0, out result,
        Tuple.Create("score", "0.8"));
}
```

## 前端订阅图像事件

若前端按 `IFrontendVisionService` / `FrontendVisionService` 模式集成，可直接订阅 `GrabImageSucceeded`（`byte[]` 图像数据）：

```csharp
using ShareMemRPCLite;

public sealed class FrontendPage : IDisposable
{
    private readonly CallGVision gv;
    private readonly FrontendVisionService frontendVision;

    public FrontendPage()
    {
        gv = new CallGVision(isInvokeGVision: true);
        frontendVision = new FrontendVisionService(gv);

        // 1) 订阅图像事件
        frontendVision.GrabImageSucceeded += FrontendVision_GrabImageSucceeded;

        // 2) 开启相机图像推送
        gv.SetReceiveBitmapCamIndex(0, true);
    }

    private void FrontendVision_GrabImageSucceeded(object sender, GrabImageSucceededEventArgs e)
    {
        byte[] imageBytes = e.ImageBytes;
        int camId = e.CamID;

        // 在前端控件中渲染 imageBytes（WPF/WinForms/WebView）。
    }

    public void RunTabAndWaitImage()
    {
        // 可选：先请求一次图像帧
        gv.ReceiveShowImageOnce(0);

        SGVisionRtn result;
        gv.RunAndWaitRst("FindModel", 0, out result, Tuple.Create("score", "0.8"));
    }

    public void Dispose()
    {
        frontendVision.GrabImageSucceeded -= FrontendVision_GrabImageSucceeded;
        frontendVision.Dispose();
        gv.Dispose();
    }
}
```

事件链路：

`GVisionQt 图像 -> CallGVision.WhenReceiveBitmap -> FrontendVisionService（Bitmap 转 byte[]）-> GrabImageSucceeded`

## 编译

```powershell
dotnet msbuild RPC.CShape\ShareMemRPCLite\GKG.ShareMemRPCLite.csproj /t:Build /p:Configuration=Debug /p:Platform=AnyCPU
```
