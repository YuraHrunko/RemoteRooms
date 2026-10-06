using System.Windows;

namespace RemoteRooms;

internal static class VncScreenLayout
{
    // Many VNC servers publish several equal-sized monitors as one framebuffer.
    // RFB gives this client no monitor rectangles, so infer only plausible equal splits.
    public static IReadOnlyList<Int32Rect> Infer(int width, int height)
    {
        var bestCount = 1;
        var vertical = false;
        var bestScore = double.MaxValue;
        for (var count = 2; count <= 6; count++)
        {
            if (width % count == 0 && width / count >= 800 && height >= 600)
                Consider(count, false, width / (double)count / height);
            if (height % count == 0 && height / count >= 600 && width >= 800)
                Consider(count, true, width / (double)(height / count));
        }
        if (bestCount == 1) return [];
        var result = new List<Int32Rect>(bestCount);
        for (var i = 0; i < bestCount; i++)
            result.Add(vertical
                ? new Int32Rect(0, i * (height / bestCount), width, height / bestCount)
                : new Int32Rect(i * (width / bestCount), 0, width / bestCount, height));
        return result;

        void Consider(int count, bool candidateVertical, double aspect)
        {
            if (aspect < 1.3 || aspect > 2.2) return;
            var score = Math.Min(Math.Abs(Math.Log(aspect / (16.0 / 9))),
                Math.Abs(Math.Log(aspect / (4.0 / 3)))) + count * 0.015;
            if (score < bestScore)
            {
                bestScore = score;
                bestCount = count;
                vertical = candidateVertical;
            }
        }
    }
}
