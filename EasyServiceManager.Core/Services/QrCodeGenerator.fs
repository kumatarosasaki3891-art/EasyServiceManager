namespace EasyServiceManager.Services

open System
open System.Text

/// <summary>ISO/IEC 18004 準拠の軽量 QR コード行列生成および端末描画モジュール（外部ライブラリ・UI非依存）。</summary>
module QrCodeCore =
    type private VersionSpec =
        { Version: int
          EccFormatBits: int // L = 0b01, M = 0b00
          TotalCodewords: int
          DataCodewords: int
          EcCodewords: int
          AlignmentCenters: int array }

    // URL長（最大106バイト）まで単一ブロックで確実にエンコード可能な Version 1〜5 定義
    let private specs =
        [| { Version = 1; EccFormatBits = 0b00; TotalCodewords = 26; DataCodewords = 16; EcCodewords = 10; AlignmentCenters = [||] }
           { Version = 2; EccFormatBits = 0b00; TotalCodewords = 44; DataCodewords = 28; EcCodewords = 16; AlignmentCenters = [| 6; 18 |] }
           { Version = 3; EccFormatBits = 0b00; TotalCodewords = 70; DataCodewords = 44; EcCodewords = 26; AlignmentCenters = [| 6; 22 |] }
           { Version = 3; EccFormatBits = 0b01; TotalCodewords = 70; DataCodewords = 55; EcCodewords = 15; AlignmentCenters = [| 6; 22 |] }
           { Version = 4; EccFormatBits = 0b01; TotalCodewords = 100; DataCodewords = 80; EcCodewords = 20; AlignmentCenters = [| 6; 26 |] }
           { Version = 5; EccFormatBits = 0b01; TotalCodewords = 134; DataCodewords = 108; EcCodewords = 26; AlignmentCenters = [| 6; 30 |] } |]

    /// <summary>ビット配列の末尾へ指定ビット数の整数値を上位ビットから追加します。</summary>
    let private appendBits (bits: ResizeArray<bool>) (value: int) (bitCount: int) =
        for i = bitCount - 1 downto 0 do
            bits.Add(((value >>> i) &&& 1) <> 0)

    /// <summary>バイト長に対応する最小の QR バージョン仕様を選択します。</summary>
    let private selectSpec (byteLength: int) =
        specs
        |> Array.tryFind (fun s -> byteLength + 2 <= s.DataCodewords)
        |> Option.defaultWith (fun () -> raise (ArgumentOutOfRangeException("byteLength", "QRコードに格納可能な最大文字数を超えています。")))

    /// <summary>GF(2^8) 有限体上の乗算を行います（原始多項式 0x11D）。</summary>
    let private gfMultiply (x: byte) (y: byte) : byte =
        let mutable z = 0
        for i = 7 downto 0 do
            z <- (z <<< 1) ^^^ ((z >>> 7) * 0x11D)
            z <- z ^^^ (((int y >>> i) &&& 1) * int x)
        byte z

    /// <summary>リード・ソロモン誤り訂正符号用の生成多項式を構築します。</summary>
    let private buildGeneratorPolynomial (degree: int) =
        let poly = Array.zeroCreate<byte> degree
        poly.[degree - 1] <- 1uy
        let mutable root = 1uy
        for _ = 0 to degree - 1 do
            for j = 0 to degree - 1 do
                poly.[j] <- gfMultiply poly.[j] root
                if j + 1 < degree then
                    poly.[j] <- poly.[j] ^^^ poly.[j + 1]
            root <- gfMultiply root 0x02uy
        poly

    /// <summary>データコード語列に対してリード・ソロモン誤り訂正コード語を算出します。</summary>
    let private computeReedSolomon (data: byte array) (ecCount: int) =
        let generator = buildGeneratorPolynomial ecCount
        let remainder = Array.zeroCreate<byte> ecCount
        for b in data do
            let factor = b ^^^ remainder.[0]
            Array.Copy(remainder, 1, remainder, 0, ecCount - 1)
            remainder.[ecCount - 1] <- 0uy
            if factor <> 0uy then
                for i = 0 to ecCount - 1 do
                    remainder.[i] <- remainder.[i] ^^^ (gfMultiply generator.[i] factor)
        remainder

    /// <summary>入力バイト列を Byte モードのデータコード語配列へエンコードします。</summary>
    let private buildDataCodewords (payload: byte array) (dataCodewordsCount: int) =
        let capacityBits = dataCodewordsCount * 8
        let bits = ResizeArray<bool>(capacityBits)
        appendBits bits 0b0100 4
        appendBits bits payload.Length 8
        for b in payload do
            appendBits bits (int b) 8
        let terminatorLen = min 4 (capacityBits - bits.Count)
        appendBits bits 0 terminatorLen
        while bits.Count % 8 <> 0 do
            bits.Add(false)
        let padBytes = [| 0xEC; 0x11 |]
        let mutable padIdx = 0
        while bits.Count < capacityBits do
            appendBits bits padBytes.[padIdx % 2] 8
            padIdx <- padIdx + 1
        let result = Array.zeroCreate<byte> dataCodewordsCount
        for i = 0 to dataCodewordsCount - 1 do
            let mutable v = 0
            for b = 0 to 7 do
                v <- (v <<< 1) ||| (if bits.[i * 8 + b] then 1 else 0)
            result.[i] <- byte v
        result

    /// <summary>7x7 ファインダパターンと周囲のセパレータを配置します。</summary>
    let private placeFinderPattern (matrix: bool[,]) (isFunction: bool[,]) (topRow: int) (leftCol: int) =
        let size = Array2D.length1 matrix
        for dr = -1 to 7 do
            for dc = -1 to 7 do
                let r = topRow + dr
                let c = leftCol + dc
                if r >= 0 && r < size && c >= 0 && c < size then
                    let dark =
                        (dr >= 0 && dr <= 6 && dc >= 0 && dc <= 6) &&
                        (dr = 0 || dr = 6 || dc = 0 || dc = 6 || (dr >= 2 && dr <= 4 && dc >= 2 && dc <= 4))
                    matrix.[r, c] <- dark
                    isFunction.[r, c] <- true

    /// <summary>5x5 アライメントパターンを配置します。</summary>
    let private placeAlignmentPattern (matrix: bool[,]) (isFunction: bool[,]) (centerRow: int) (centerCol: int) =
        for dr = -2 to 2 do
            for dc = -2 to 2 do
                let r = centerRow + dr
                let c = centerCol + dc
                matrix.[r, c] <- max (abs dr) (abs dc) <> 1
                isFunction.[r, c] <- true

    /// <summary>ファインダ・タイミング・アライメント・フォーマット予約領域などの機能パターンを配置します。</summary>
    let private placeFunctionPatterns (matrix: bool[,]) (isFunction: bool[,]) (spec: VersionSpec) =
        let size = Array2D.length1 matrix
        placeFinderPattern matrix isFunction 0 0
        placeFinderPattern matrix isFunction 0 (size - 7)
        placeFinderPattern matrix isFunction (size - 7) 0

        for i = 8 to size - 9 do
            let dark = i % 2 = 0
            if not isFunction.[6, i] then
                matrix.[6, i] <- dark
                isFunction.[6, i] <- true
            if not isFunction.[i, 6] then
                matrix.[i, 6] <- dark
                isFunction.[i, 6] <- true

        let centers = spec.AlignmentCenters
        for i = 0 to centers.Length - 1 do
            for j = 0 to centers.Length - 1 do
                let overlapsFinder =
                    (i = 0 && j = 0) ||
                    (i = 0 && j = centers.Length - 1) ||
                    (i = centers.Length - 1 && j = 0)
                if not overlapsFinder then
                    placeAlignmentPattern matrix isFunction centers.[i] centers.[j]

        matrix.[size - 8, 8] <- true
        isFunction.[size - 8, 8] <- true

        for i = 0 to 8 do
            isFunction.[8, i] <- true
            isFunction.[i, 8] <- true
        for i = 0 to 7 do
            isFunction.[8, size - 1 - i] <- true
            isFunction.[size - 1 - i, 8] <- true

    /// <summary>全コード語ビットをジグザグ走査でデータ領域へ配置します。</summary>
    let private placeDataBits (matrix: bool[,]) (isFunction: bool[,]) (codewords: byte array) =
        let size = Array2D.length1 matrix
        let mutable bitIdx = 0
        let totalBits = codewords.Length * 8
        let mutable upward = true
        let mutable rightCol = size - 1
        while rightCol >= 1 do
            if rightCol = 6 then rightCol <- 5
            for vert = 0 to size - 1 do
                let r = if upward then size - 1 - vert else vert
                for j = 0 to 1 do
                    let c = rightCol - j
                    if not isFunction.[r, c] then
                        let mutable dark = false
                        if bitIdx < totalBits then
                            dark <- ((int codewords.[bitIdx >>> 3] >>> (7 - (bitIdx &&& 7))) &&& 1) <> 0
                            bitIdx <- bitIdx + 1
                        matrix.[r, c] <- dark
            upward <- not upward
            rightCol <- rightCol - 2

    /// <summary>指定マスク番号（0〜7）のマスキングをデータ領域へ適用します。</summary>
    let private applyMask (matrix: bool[,]) (isFunction: bool[,]) (mask: int) =
        let size = Array2D.length1 matrix
        for r = 0 to size - 1 do
            for c = 0 to size - 1 do
                if not isFunction.[r, c] then
                    let invert =
                        match mask with
                        | 0 -> (r + c) % 2 = 0
                        | 1 -> r % 2 = 0
                        | 2 -> c % 3 = 0
                        | 3 -> (r + c) % 3 = 0
                        | 4 -> ((r / 2) + (c / 3)) % 2 = 0
                        | 5 -> ((r * c) % 2) + ((r * c) % 3) = 0
                        | 6 -> (((r * c) % 2) + ((r * c) % 3)) % 2 = 0
                        | 7 -> (((r + c) % 2) + ((r * c) % 3)) % 2 = 0
                        | _ -> false
                    if invert then matrix.[r, c] <- not matrix.[r, c]

    /// <summary>BCH 誤り訂正付きフォーマット情報をマトリクスへ書き込みます。</summary>
    let private writeFormatBits (matrix: bool[,]) (eccFormatBits: int) (mask: int) =
        let size = Array2D.length1 matrix
        let data = (eccFormatBits <<< 3) ||| mask
        let mutable rem = data
        for _ = 0 to 9 do
            rem <- (rem <<< 1) ^^^ (((rem >>> 9) &&& 1) * 0x537)
        let bits = ((data <<< 10) ||| rem) ^^^ 0x5412
        let getBit idx = ((bits >>> idx) &&& 1) <> 0

        for i = 0 to 5 do matrix.[i, 8] <- getBit i
        matrix.[7, 8] <- getBit 6
        matrix.[8, 8] <- getBit 7
        matrix.[8, 7] <- getBit 8
        for i = 9 to 14 do matrix.[8, 14 - i] <- getBit i

        for i = 0 to 7 do matrix.[8, size - 1 - i] <- getBit i
        for i = 8 to 14 do matrix.[size - 15 + i, 8] <- getBit i
        matrix.[size - 8, 8] <- true

    /// <summary>マスク品質評価のためのペナルティスコアを算出します。</summary>
    let private calculatePenalty (matrix: bool[,]) =
        let size = Array2D.length1 matrix
        let mutable penalty = 0
        for r = 0 to size - 1 do
            let mutable runLen = 1
            for c = 1 to size - 1 do
                if matrix.[r, c] = matrix.[r, c - 1] then
                    runLen <- runLen + 1
                    if runLen = 5 then penalty <- penalty + 3
                    elif runLen > 5 then penalty <- penalty + 1
                else runLen <- 1
        for c = 0 to size - 1 do
            let mutable runLen = 1
            for r = 1 to size - 1 do
                if matrix.[r, c] = matrix.[r - 1, c] then
                    runLen <- runLen + 1
                    if runLen = 5 then penalty <- penalty + 3
                    elif runLen > 5 then penalty <- penalty + 1
                else runLen <- 1
        for r = 0 to size - 2 do
            for c = 0 to size - 2 do
                let color = matrix.[r, c]
                if matrix.[r, c + 1] = color && matrix.[r + 1, c] = color && matrix.[r + 1, c + 1] = color then
                    penalty <- penalty + 3
        penalty

    /// <summary>指定されたテキスト（URL等）を QR コードの2次元真偽値マトリクスへエンコードします。</summary>
    let encodeToMatrix (text: string) : bool[,] =
        let payload = Encoding.UTF8.GetBytes(if isNull text then "" else text)
        let spec = selectSpec payload.Length
        let dataCodewords = buildDataCodewords payload spec.DataCodewords
        let ecCodewords = computeReedSolomon dataCodewords spec.EcCodewords
        let allCodewords = Array.zeroCreate<byte> spec.TotalCodewords
        Array.Copy(dataCodewords, 0, allCodewords, 0, dataCodewords.Length)
        Array.Copy(ecCodewords, 0, allCodewords, dataCodewords.Length, ecCodewords.Length)

        let size = 17 + 4 * spec.Version
        let baseMatrix = Array2D.zeroCreate<bool> size size
        let isFunction = Array2D.zeroCreate<bool> size size
        placeFunctionPatterns baseMatrix isFunction spec
        placeDataBits baseMatrix isFunction allCodewords

        let mutable minPenalty = Int32.MaxValue
        let mutable bestMatrix: bool[,] = null
        for mask = 0 to 7 do
            let candidate = baseMatrix.Clone() :?> bool[,]
            applyMask candidate isFunction mask
            writeFormatBits candidate spec.EccFormatBits mask
            let p = calculatePenalty candidate
            if p < minPenalty then
                minPenalty <- p
                bestMatrix <- candidate
        bestMatrix

    /// <summary>半ブロック文字を使って QR コードを端末向け文字列に描画します（クワイエットゾーン4モジュール）。</summary>
    let renderToTerminal (text: string) (useAnsiColor: bool) : string =
        let matrix = encodeToMatrix text
        let size = Array2D.length1 matrix
        let quiet = 4
        let total = size + quiet * 2
        let isDark row col =
            let r = row - quiet
            let c = col - quiet
            r >= 0 && r < size && c >= 0 && c < size && matrix.[r, c]

        let sb = StringBuilder()
        let mutable row = 0
        while row < total do
            if useAnsiColor then sb.Append("\u001b[30;47m") |> ignore
            for col = 0 to total - 1 do
                let top = isDark row col
                let bottom = if row + 1 < total then isDark (row + 1) col else false
                let ch =
                    match top, bottom with
                    | true, true -> '█'
                    | true, false -> '▀'
                    | false, true -> '▄'
                    | false, false -> ' '
                sb.Append(ch) |> ignore
            if useAnsiColor then sb.Append("\u001b[0m") |> ignore
            if row + 2 < total then sb.AppendLine() |> ignore
            row <- row + 2
        sb.ToString()
