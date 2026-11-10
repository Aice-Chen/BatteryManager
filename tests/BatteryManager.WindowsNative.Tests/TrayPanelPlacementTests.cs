using BatteryManager.App;
using Xunit;

namespace BatteryManager.WindowsNative.Tests;

public sealed class TrayPanelPlacementTests
{
    [Theory]
    [InlineData(1, 360, 194, 12)]
    [InlineData(1.5, 540, 291, 18)]
    [InlineData(2, 720, 388, 24)]
    public void Bottom_taskbar_uses_scaled_size_and_bottom_right_work_area_margins(
        double scale, int expectedWidth, int expectedHeight, int margin)
    {
        var work = new DisplayRect(0, 0, 1920, 1032);
        var result = PanelPlacement.Calculate(work, new(1840, 1032, 32, 48), scale, 3, false);
        Assert.Equal(new DisplayRect(1920 - margin - expectedWidth, 1032 - margin - expectedHeight,
            expectedWidth, expectedHeight), result);
        AssertContained(work, result);
    }

    [Fact]
    public void Top_taskbar_opens_below_work_area_top_and_stays_right_aligned()
    {
        var work = new DisplayRect(0, 48, 1920, 1032);
        var result = PanelPlacement.Calculate(work, new(1840, 0, 32, 48), 1, 3, false);
        Assert.Equal(new DisplayRect(1548, 60, 360, 194), result);
        AssertContained(work, result);
    }

    [Theory]
    [InlineData(70, 70)]
    [InlineData(-10, 12)]
    [InlineData(1048, 874)]
    public void Left_taskbar_aligns_left_and_clamps_anchor_height(int anchorY, int expectedY)
    {
        var work = new DisplayRect(48, 0, 1872, 1080);
        var result = PanelPlacement.Calculate(work, new(0, anchorY, 48, 32), 1, 3, false);
        Assert.Equal(new DisplayRect(60, expectedY, 360, 194), result);
        AssertContained(work, result);
    }

    [Theory]
    [InlineData(70, 70)]
    [InlineData(-10, 12)]
    [InlineData(1048, 874)]
    public void Right_taskbar_aligns_right_and_clamps_anchor_height(int anchorY, int expectedY)
    {
        var work = new DisplayRect(0, 0, 1872, 1080);
        var result = PanelPlacement.Calculate(work, new(1872, anchorY, 48, 32), 1, 3, false);
        Assert.Equal(new DisplayRect(1500, expectedY, 360, 194), result);
        AssertContained(work, result);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void Secondary_monitor_with_negative_coordinates_does_not_move_to_primary(double scale)
    {
        var work = new DisplayRect(-2560, -1440, 2560, 1392);
        var result = PanelPlacement.Calculate(work, new(-100, -48, 32, 48), scale, 3, true);
        AssertContained(work, result);
        Assert.True(result.X < 0);
        Assert.True(result.Y < 0);
        var margin = (int)Math.Round(12 * scale);
        Assert.Equal(work.Right - margin, result.Right);
        Assert.Equal(work.Bottom - margin, result.Bottom);
        Assert.Equal((int)Math.Round(222 * scale), result.Height);
    }

    [Theory]
    [InlineData(0, false, 98)]
    [InlineData(1, false, 98)]
    [InlineData(1, true, 126)]
    [InlineData(3, true, 222)]
    public void Empty_view_has_one_row_space_and_demo_adds_banner_height(int count, bool demo, int expectedHeight)
    {
        var result = PanelPlacement.Calculate(new(0, 0, 1920, 1032), new(1840, 1032, 32, 48), 1, count, demo);
        Assert.Equal(expectedHeight, result.Height);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(30)]
    [InlineData(int.MaxValue)]
    public void More_than_three_rows_keep_the_three_row_height_and_scroll(int count)
    {
        var work = new DisplayRect(0, 0, 1920, 1032);
        var three = PanelPlacement.Calculate(work, new(1840, 1032, 32, 48), 1, 3, false);
        var many = PanelPlacement.Calculate(work, new(1840, 1032, 32, 48), 1, count, false);
        Assert.Equal(three, many);
        Assert.Equal(194, many.Height);
    }

    [Theory]
    [InlineData(1, 98)]
    [InlineData(2, 146)]
    [InlineData(3, 194)]
    public void Up_to_three_rows_size_exactly_to_their_rows(int count, int expectedHeight)
    {
        var result = PanelPlacement.Calculate(new(0, 0, 1920, 1032), new(1840, 1032, 32, 48), 1, count, false);
        Assert.Equal(expectedHeight, result.Height);
    }

    [Theory]
    [InlineData(1, 0, 147)]
    [InlineData(1, 2, 149)]
    [InlineData(3, 2, 293)]
    public void Window_frame_is_added_so_the_client_area_fits_the_content(int count, int frame, int expectedHeight)
    {
        var work = new DisplayRect(0, 0, 2880, 1548);
        var result = PanelPlacement.Calculate(work, new(2760, 1548, 48, 72), 1.5, count, false, frame);
        Assert.Equal(expectedHeight, result.Height);
        Assert.Equal(work.Bottom - 18, result.Bottom);
        AssertContained(work, result);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void Many_rows_still_fit_a_short_work_area(double scale)
    {
        var work = new DisplayRect(-1280, 300, 1280, 200);
        var result = PanelPlacement.Calculate(work, new(-100, 500, 32, 48), scale, int.MaxValue, true);
        var margin = (int)Math.Round(12 * scale);
        Assert.Equal(Math.Min(200 - 2 * margin, (int)Math.Round(222 * scale)), result.Height);
        AssertContained(work, result);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 3)]
    [InlineData(10, 10)]
    [InlineData(20, 15)]
    [InlineData(30, 40)]
    [InlineData(300, 180)]
    public void Tiny_work_areas_shrink_margins_and_never_return_an_out_of_bounds_window(int width, int height)
    {
        var work = new DisplayRect(-100, -100, width, height);
        var anchors = new[]
        {
            new DisplayRect(-120, -90, 20, 20),
            new DisplayRect(-100 + width, -90, 20, 20),
            new DisplayRect(-90, -120, 20, 20),
            new DisplayRect(-90, -100 + height, 20, 20)
        };
        foreach (var scale in new[] { 1d, 1.5d, 2d })
        foreach (var anchor in anchors)
            AssertContained(work, PanelPlacement.Calculate(work, anchor, scale, 30, true));
    }

    [Fact]
    public void In_work_area_cursor_fallback_keeps_bottom_right_position()
    {
        var work = new DisplayRect(-1280, 0, 1280, 984);
        Assert.Equal(new DisplayRect(-372, 874, 360, 98),
            PanelPlacement.Calculate(work, new(-600, 500, 1, 1), 1, 0, false));
    }

    [Fact]
    public void Invalid_monitor_and_scale_values_are_rejected()
    {
        var work = new DisplayRect(0, 0, 1920, 1080);
        var anchor = new DisplayRect(1800, 1080, 32, 48);
        Assert.Throws<ArgumentOutOfRangeException>(() => PanelPlacement.Calculate(work with { Width = 0 }, anchor, 1, 1, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => PanelPlacement.Calculate(work, anchor, 0, 1, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => PanelPlacement.Calculate(work, anchor, double.NaN, 1, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => PanelPlacement.Calculate(work, anchor, double.PositiveInfinity, 1, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => PanelPlacement.Calculate(work, anchor, 1, -1, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => PanelPlacement.Calculate(work, anchor, 1, 1, false, -1));
    }

    private static void AssertContained(DisplayRect work, DisplayRect panel)
    {
        Assert.InRange(panel.Width, 1, work.Width);
        Assert.InRange(panel.Height, 1, work.Height);
        Assert.True(panel.X >= work.X && panel.Y >= work.Y);
        Assert.True(panel.Right <= work.Right && panel.Bottom <= work.Bottom);
    }
}
