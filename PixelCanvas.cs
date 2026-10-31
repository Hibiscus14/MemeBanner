using UnityEngine;

internal static class PixelCanvas
{
    internal static void ClearRect(Color32[] canvas, int width, int height, int left, int bottom, int w, int h)
    {
        for (int y = 0; y < h; y++)
        {
            int canvasY = bottom + y;
            if (canvasY < 0 || canvasY >= height)
            {
                continue;
            }
            for (int x = 0; x < w; x++)
            {
                int canvasX = left + x;
                if (canvasX >= 0 && canvasX < width)
                {
                    canvas[canvasY * width + canvasX] = new Color32(0, 0, 0, 0);
                }
            }
        }
    }
}
