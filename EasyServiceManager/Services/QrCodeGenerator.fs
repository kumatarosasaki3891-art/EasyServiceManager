namespace EasyServiceManager.Services

open System.Runtime.InteropServices
open Avalonia
open Avalonia.Media.Imaging
open Avalonia.Platform

/// <summary>GUI 向け QR コード Bitmap 生成モジュール（行列生成は Core の QrCodeCore を利用）。</summary>
module QrCodeGenerator =
    /// <summary>指定されたテキスト（URL等）を QR コードの2次元真偽値マトリクスへエンコードします。</summary>
    let encodeToMatrix (text: string) : bool[,] =
        QrCodeCore.encodeToMatrix text

    /// <summary>指定されたテキストから Avalonia 表示用の QR コード WriteableBitmap を生成します。</summary>
    let generateBitmap (text: string) (pixelsPerModule: int) (quietZoneModules: int) : WriteableBitmap =
        let matrix = QrCodeCore.encodeToMatrix text
        let size = Array2D.length1 matrix
        let totalModules = size + quietZoneModules * 2
        let imgSize = totalModules * pixelsPerModule

        let bitmap = new WriteableBitmap(PixelSize(imgSize, imgSize), Vector(96.0, 96.0), PixelFormat.Bgra8888, AlphaFormat.Opaque)
        use fb = bitmap.Lock()
        let stride = fb.RowBytes
        let buffer = Array.create<byte> (stride * imgSize) 0xFFuy

        for r = 0 to size - 1 do
            for c = 0 to size - 1 do
                if matrix.[r, c] then
                    let startY = (r + quietZoneModules) * pixelsPerModule
                    let startX = (c + quietZoneModules) * pixelsPerModule
                    for dy = 0 to pixelsPerModule - 1 do
                        let rowOffset = (startY + dy) * stride
                        for dx = 0 to pixelsPerModule - 1 do
                            let px = rowOffset + (startX + dx) * 4
                            buffer.[px] <- 0x00uy
                            buffer.[px + 1] <- 0x00uy
                            buffer.[px + 2] <- 0x00uy
                            buffer.[px + 3] <- 0xFFuy

        Marshal.Copy(buffer, 0, fb.Address, buffer.Length)
        bitmap
