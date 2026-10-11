using System.Runtime.InteropServices;
using ScreenTranslator.Core.Models;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace ScreenTranslator.App.Services.Capture;

/// <summary>One GPU frame, copied to an owned BGRA buffer. No picker, cached desktop or UI-thread dependency.</summary>
public static class GraphicsFrameReader
{
    public static async Task<CapturedBitmap> CaptureAsync(nint handle,bool monitor,CancellationToken token,bool waitForContent=false)
    {
        token.ThrowIfCancellationRequested();
        using var resources=new Resources(waitForContent);
        return await resources.ReadAsync(handle,monitor,token).ConfigureAwait(false);
    }
    private sealed class Resources(bool waitForContent):IDisposable
    {
        private readonly object _gpu=new();
        private ID3D11Device? _nativeDevice;private ID3D11DeviceContext? _context;private IDirect3DDevice? _device;
        private Direct3D11CaptureFramePool? _pool;private GraphicsCaptureSession? _session;private GraphicsCaptureItem? _item;
        private bool _disposed;private readonly TaskCompletionSource<CapturedBitmap> _ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<CapturedBitmap> ReadAsync(nint handle,bool monitor,CancellationToken token)
        {
            var result=D3D11.D3D11CreateDevice(null,DriverType.Hardware,DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_1,FeatureLevel.Level_11_0],out _nativeDevice);
            result.CheckError();_context=_nativeDevice!.ImmediateContext;
            using(var dxgi=_nativeDevice.QueryInterface<IDXGIDevice>())
            {
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer,out var abi));
                try{_device=MarshalInterface<IDirect3DDevice>.FromAbi(abi);}finally{Marshal.Release(abi);}
            }
            using var factory=ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
            var iid=new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");Marshal.ThrowExceptionForHR(Marshal.QueryInterface(factory.ThisPtr,ref iid,out var interopPtr));
            try
            {
                var interop=(ICaptureInterop)Marshal.GetObjectForIUnknown(interopPtr);var captureIid=new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
                var abi=monitor?interop.CreateForMonitor(handle,ref captureIid):interop.CreateForWindow(handle,ref captureIid);
                try{_item=MarshalInspectable<GraphicsCaptureItem>.FromAbi(abi);}finally{Marshal.Release(abi);}
            }
            finally{Marshal.Release(interopPtr);}
            if(_item.Size.Width<1||_item.Size.Height<1)throw new InvalidOperationException("捕获对象没有有效尺寸。");
            _pool=Direct3D11CaptureFramePool.CreateFreeThreaded(_device,DirectXPixelFormat.B8G8R8A8UIntNormalized,2,_item.Size);
            _pool.FrameArrived+=FrameArrived;_item.Closed+=ItemClosed;_session=_pool.CreateCaptureSession(_item);_session.IsCursorCaptureEnabled=false;
            _session.StartCapture();return _ready.Task.WaitAsync(TimeSpan.FromSeconds(2),token);
        }
        private void ItemClosed(GraphicsCaptureItem sender,object args)=>_ready.TrySetException(new InvalidOperationException("捕获窗口已经关闭。"));
        private void FrameArrived(Direct3D11CaptureFramePool sender,object args)
        {
            lock(_gpu)
            {
                if(_disposed||_ready.Task.IsCompleted)return;
                try
                {
                    using var frame=sender.TryGetNextFrame();if(frame is null)return;
                    var surface=(IWinRTObject)frame.Surface;var accessIid=new Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
                    Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surface.NativeObject.ThisPtr,ref accessIid,out var access));
                    try
                    {
                        var call=Marshal.GetDelegateForFunctionPointer<GetTexture>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(access),3*IntPtr.Size));
                        var textureIid=new Guid("6F15AAF2-D208-4E89-9AB4-489535D34F9C");Marshal.ThrowExceptionForHR(call(access,ref textureIid,out var pointer));
                        using var texture=new ID3D11Texture2D(pointer);var desc=texture.Description;
                        var width=Math.Min((int)desc.Width,frame.ContentSize.Width);var height=Math.Min((int)desc.Height,frame.ContentSize.Height);
                        if(width<1||height<1||width*(long)height>64_000_000)throw new InvalidOperationException("GPU 帧尺寸无效或过大。");
                        desc.Usage=ResourceUsage.Staging;desc.BindFlags=BindFlags.None;desc.CPUAccessFlags=CpuAccessFlags.Read;desc.MiscFlags=ResourceOptionFlags.None;desc.MipLevels=1;desc.ArraySize=1;
                        using var staging=_nativeDevice!.CreateTexture2D(desc);_context!.CopyResource(staging,texture);
                        var mapped=_context.Map(staging,0,MapMode.Read,Vortice.Direct3D11.MapFlags.None);
                        try
                        {
                            var pixels=new byte[checked(width*height*4)];for(var y=0;y<height;y++)Marshal.Copy(mapped.DataPointer+y*(int)mapped.RowPitch,pixels,y*width*4,width*4);
                            if(waitForContent&&!pixels.Where((value,index)=>index%4!=3).Any(value=>value>8))return;
                            for(var i=3;i<pixels.Length;i+=4)pixels[i]=255;
                            _ready.TrySetResult(new(width,height,width*4,pixels));
                        }
                        finally{_context.Unmap(staging,0);}
                    }
                    finally{Marshal.Release(access);}
                }
                catch(Exception error){_ready.TrySetException(error);}
            }
        }
        public void Dispose()
        {
            lock(_gpu){if(_disposed)return;_disposed=true;}
            // Remove callbacks before disposing their frame pool; a callback never
            // waits for shutdown while holding the GPU lock.
            if(_pool is not null)_pool.FrameArrived-=FrameArrived;if(_item is not null)_item.Closed-=ItemClosed;
            _session?.Dispose();_pool?.Dispose();lock(_gpu){_context?.Dispose();_nativeDevice?.Dispose();(_device as IDisposable)?.Dispose();}
        }
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]private delegate int GetTexture(nint self,ref Guid iid,out nint result);
    [ComImport,Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICaptureInterop{nint CreateForWindow(nint window,ref Guid iid);nint CreateForMonitor(nint monitor,ref Guid iid);}
    [DllImport("d3d11.dll",ExactSpelling=true)]private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint device,out nint result);
}
