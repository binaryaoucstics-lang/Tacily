using ScreenTranslator.App.Services.Capture;
using ScreenTranslator.Core.Models;

namespace ScreenTranslator.IntegrationTests.Capture;

public sealed class MouseSelectionServiceTests
{
    [Fact]
    public async Task Selection_Click_Is_Consumed_But_Popup_Controls_Remain_Clickable()
    {
        using var service = new MouseSelectionService(CancellationToken.None, installHooks: false);
        var point = new PixelPoint(-1400, 240);
        Assert.False(service.HandleMouseMessage(0x0201, point, isOurWindow: true));
        Assert.False(service.Selection.IsCompleted);
        Assert.True(service.HandleMouseMessage(0x0201, point, isOurWindow: false));
        Assert.True(service.HandleMouseMessage(0x0202, point, isOurWindow: false));
        var selected = await service.Selection;
        Assert.False(selected.IsDrag);
        Assert.Equal(point, selected.Point);
    }

    [Fact]
    public async Task Reverse_Drag_Has_Physical_Bounds_And_Consumes_Both_Button_Events()
    {
        using var service = new MouseSelectionService(CancellationToken.None, installHooks: false);
        PixelRect preview = default;
        service.DragChanged += rect => preview = rect;
        Assert.True(service.HandleMouseMessage(0x0201, new PixelPoint(900, 400), false));
        Assert.False(service.HandleMouseMessage(0x0200, new PixelPoint(500, 200), true));
        Assert.True(service.HandleMouseMessage(0x0202, new PixelPoint(500, 200), false));
        var result = await service.Selection;
        Assert.True(result.IsDrag);
        Assert.Equal(new PixelRect(500, 200, 400, 200), result.Bounds);
        Assert.Equal(result.Bounds, preview);
    }

    [Fact]
    public async Task Fast_Drag_Passes_All_Movement_Through_And_Uses_The_Release_Position()
    {
        using var service = new MouseSelectionService(CancellationToken.None, installHooks: false);
        Assert.True(service.HandleMouseMessage(0x0201, new PixelPoint(100, 100), false));
        for (var step = 1; step <= 1000; step++)
            Assert.False(service.HandleMouseMessage(0x0200, new PixelPoint(100 + step, 100 + step / 2), true));
        Assert.True(service.HandleMouseMessage(0x0202, new PixelPoint(1200, 700), true));
        var result = await service.Selection;
        Assert.True(result.IsDrag);
        Assert.Equal(new PixelRect(100, 100, 1100, 600), result.Bounds);
    }

    [Fact]
    public async Task Right_Click_Cancels_Without_Forwarding_A_Context_Menu_Click()
    {
        using var service = new MouseSelectionService(CancellationToken.None, installHooks: false);
        Assert.True(service.HandleMouseMessage(0x0204, new PixelPoint(100, 100), false));
        Assert.True(service.HandleMouseMessage(0x0205, new PixelPoint(100, 100), false));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.Selection);
    }
}
