using System.Runtime.InteropServices;
using OpenCvSharp;
using ProjectTabletop.Vision;

// Physical-board, projector-field, corner-marker and calibration-spot detection checks.
internal static class BoardDetectionRegression
{
    internal static string Format(BoardDetection? detection) => detection is null ? "not found" :
        string.Join(", ", detection.Corners.Select(p => $"({p.X:F1},{p.Y:F1})"));

    internal static void CheckProjectedCornerMarkers(string? extraMarkerFrame = null)
    {
        byte[] ReadFixture(string name, out int frameWidth, out int frameHeight,
            out int frameStride)
        {
            string path = Path.IsPathRooted(name) ? name :
                Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
            using Mat original = Cv2.ImRead(path, ImreadModes.Color);
            if (original.Empty()) throw new Exception($"Missing marker fixture: {path}");
            using Mat camera = new();
            Cv2.CvtColor(original, camera, ColorConversionCodes.BGR2BGRA);
            frameWidth = camera.Width;
            frameHeight = camera.Height;
            frameStride = (int)camera.Step();
            return BytesOf(camera);
        }

        byte[] ambientPixels = ReadFixture("moved-cardboard-ambient.png",
            out int width, out int height, out int stride);
        BoardDetection? board = BoardDetector.DetectAmbientBoard(width, height,
            stride, ambientPixels);
        if (board is null) throw new Exception("Marker fixture has no ambient board reference.");

        var names = new List<string> { "moved-cardboard-projected-grid.png",
            "moved-cardboard-projected-grid-repeat.png" };
        if (extraMarkerFrame is not null) names.Add(extraMarkerFrame);
        foreach (string name in names)
        {
            byte[] pixels = ReadFixture(name, out int gridWidth, out int gridHeight,
                out int gridStride);
            if (gridWidth != width || gridHeight != height || gridStride != stride)
                throw new Exception("Projected-grid fixture dimensions changed.");
            CornerMarkerDetection?[] measured = ProjectedCornerDetector.Detect(
                width, height, stride, pixels, board.Corners);
            int validMarkers = 0;
            for (int corner = 0; corner < 4; corner++)
            {
                CornerMarkerDetection? marker = measured[corner];
                if (marker is null)
                {
                    Console.WriteLine($"{Path.GetFileName(name)}, corner {corner}: " +
                        "marker rejected (arm clipped or occluded)");
                    continue;
                }
                validMarkers++;
                double dx = marker.Intersection.X - board.Corners[corner].X;
                double dy = marker.Intersection.Y - board.Corners[corner].Y;
                Console.WriteLine($"{Path.GetFileName(name)}, corner {corner}: marker=" +
                    $"({marker.Intersection.X:F1},{marker.Intersection.Y:F1}); " +
                    $"board residual=({dx:+0.0;-0.0},{dy:+0.0;-0.0}) px; " +
                    $"arm RMS=({marker.FirstArmRmsPixels:F1},{marker.SecondArmRmsPixels:F1}) px; " +
                    $"confidence={marker.Confidence:F2}");
                if (Math.Sqrt(dx * dx + dy * dy) > 9 || marker.Confidence < 0.25)
                    throw new Exception($"Unreliable projected marker {corner} in {name}.");
            }
            if (validMarkers < 3 ||
                (name == "moved-cardboard-projected-grid.png" && validMarkers != 4))
                throw new Exception($"Too few projected markers in {name}: {validMarkers}.");

            // Cover most of the first top-left arm. A lone vertical stroke must
            // not produce a correction, while the other three corners stay valid.
            if (name == "moved-cardboard-projected-grid.png")
            {
                byte[] occluded = (byte[])pixels.Clone();
                for (int y = 91; y <= 123; y++)
                    for (int x = 625; x <= 678; x++)
                    {
                        int offset = y * stride + x * 4;
                        occluded[offset] = occluded[offset + 1] = occluded[offset + 2] = 30;
                    }
                CornerMarkerDetection?[] hidden = ProjectedCornerDetector.Detect(
                    width, height, stride, occluded, board.Corners);
                if (hidden[0] is not null || hidden.Skip(1).Any(marker => marker is null))
                    throw new Exception("Occluded orange marker was not rejected independently.");
            }
        }

        if (ProjectedCornerDetector.Detect(width, height, stride,
            ambientPixels, board.Corners).Any(marker => marker is not null))
            throw new Exception("Ambient cardboard edges were mislabeled orange markers.");
        Console.WriteLine("Projected corner feedback regression: live grid captures detected; " +
            "ambient-only and occluded-arm cases rejected");
    }

    internal static void CheckUniformIllumination()
    {
        const int sceneWidth = 960, sceneHeight = 540;
        using Mat image = new(sceneHeight, sceneWidth, MatType.CV_8UC4,
            new Scalar(20, 20, 20, 255));
        Point[] field = [new(100, 38), new(872, 42), new(880, 500), new(95, 496)];
        Point[] board = [new(205, 152), new(804, 163), new(791, 386), new(194, 377)];
        Cv2.FillConvexPoly(image, field, new Scalar(170, 80, 35, 255));
        Cv2.FillConvexPoly(image, board, new Scalar(214, 211, 207, 255));
        BoardDetection? fieldFound = BoardDetector.DetectProjectedField(sceneWidth, sceneHeight,
            sceneWidth * 4, BytesOf(image));
        BoardDetection? boardFound = BoardDetector.DetectUniformIllumination(sceneWidth, sceneHeight,
            sceneWidth * 4, BytesOf(image));
        if (!Near(fieldFound, field, 18) || !Near(boardFound, board, 18))
            throw new Exception($"White-scan corners failed: field={Format(fieldFound)}, board={Format(boardFound)}");

        using Mat noBoard = new(sceneHeight, sceneWidth, MatType.CV_8UC4,
            new Scalar(20, 20, 20, 255));
        Cv2.FillConvexPoly(noBoard, field, new Scalar(170, 80, 35, 255));
        if (BoardDetector.DetectUniformIllumination(sceneWidth, sceneHeight,
            sceneWidth * 4, BytesOf(noBoard)) is not null)
            throw new Exception("Uniform projector field alone was mislabeled as cardboard.");

        using Mat darkBoard = new(sceneHeight, sceneWidth, MatType.CV_8UC4,
            new Scalar(20, 20, 20, 255));
        Cv2.FillConvexPoly(darkBoard, field, new Scalar(205, 205, 205, 255));
        Cv2.FillConvexPoly(darkBoard, board, new Scalar(81, 78, 75, 255));
        if (!Near(BoardDetector.DetectUniformIllumination(sceneWidth, sceneHeight,
            sceneWidth * 4, BytesOf(darkBoard)), board, 18))
            throw new Exception("Darker cardboard under uniform illumination was missed.");
        Console.WriteLine("White-scan regression: independent wide cardboard and light-field quads, " +
            "bright and dark cardboard; field-only rejected");
    }

    internal static BoardDetection CheckHardwareWhiteScan()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "white-cardboard-full-field.png");
        using Mat original = Cv2.ImRead(path, ImreadModes.Color);
        if (original.Empty()) throw new Exception($"Missing white-scan hardware regression frame: {path}");
        using Mat camera = new();
        Cv2.CvtColor(original, camera, ColorConversionCodes.BGR2BGRA);
        byte[] pixels = BytesOf(camera);
        BoardDetection? field = BoardDetector.DetectProjectedField(camera.Width, camera.Height,
            (int)camera.Step(), pixels);
        BoardDetection? board = BoardDetector.DetectUniformIllumination(camera.Width, camera.Height,
            (int)camera.Step(), pixels);
        Point[] expectedField = [new(368, 106), new(1879, 84), new(1895, 934), new(378, 956)];
        Point[] expectedBoard = [new(624, 106), new(1690, 93), new(1707, 931), new(632, 949)];
        if (!Near(field, expectedField, 16) || !Near(board, expectedBoard, 16))
            throw new Exception($"Physical cardboard hardware regression failed: " +
                $"field={Format(field)}, cardboard={Format(board)}");
        Console.WriteLine($"Hardware white-scan regression: field={Format(field)}; " +
            $"cardboard={Format(board)}");
        return board!;
    }

    internal static void CheckHardwareAmbientScan(BoardDetection whiteBoard)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ambient-cardboard-black-output.png");
        using Mat original = Cv2.ImRead(path, ImreadModes.Color);
        if (original.Empty()) throw new Exception($"Missing ambient cardboard regression frame: {path}");
        using Mat camera = new();
        Cv2.CvtColor(original, camera, ColorConversionCodes.BGR2BGRA);
        BoardDetection? ambient = BoardDetector.DetectAmbientBoard(camera.Width, camera.Height,
            (int)camera.Step(), BytesOf(camera));
        Point[] expectedBoard = [new(624, 106), new(1690, 93), new(1707, 931), new(632, 949)];
        if (!Near(ambient, expectedBoard, 16) || ambient is null ||
            Enumerable.Range(0, 4).Any(i =>
                Math.Sqrt(Math.Pow(ambient.Corners[i].X - whiteBoard.Corners[i].X, 2) +
                    Math.Pow(ambient.Corners[i].Y - whiteBoard.Corners[i].Y, 2)) > 14))
            throw new Exception($"Black-output ambient board did not match white-scan board: {Format(ambient)}");
        Console.WriteLine($"Hardware ambient regression: cardboard={Format(ambient)}; " +
            "all corners agree with white scan within 14 px");
    }

    internal static void CheckMovedHardwareFrames(BoardDetection originalBoard)
    {
        byte[] ReadFixture(string name, out int width, out int height, out int stride)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
            using Mat image = Cv2.ImRead(path, ImreadModes.Color);
            if (image.Empty()) throw new Exception($"Missing moved-board hardware frame: {path}");
            using Mat camera = new();
            Cv2.CvtColor(image, camera, ColorConversionCodes.BGR2BGRA);
            width = camera.Width;
            height = camera.Height;
            stride = (int)camera.Step();
            return BytesOf(camera);
        }

        byte[] black = ReadFixture("moved-cardboard-ambient.png",
            out int width, out int height, out int stride);
        byte[] white = ReadFixture("moved-cardboard-white-spot.png",
            out int whiteWidth, out int whiteHeight, out int whiteStride);
        if (width != whiteWidth || height != whiteHeight || stride != whiteStride)
            throw new Exception("Moved-board camera frames have different dimensions.");
        BoardDetection? ambient = BoardDetector.DetectAmbientBoard(width, height, stride, black);
        BoardDetection? projected = BoardDetector.DetectUniformIllumination(width, height, stride, white);
        Point[] expected = [new(604, 107), new(1669, 92), new(1687, 931), new(611, 946)];
        if (!Near(ambient, expected, 5) || !Near(projected, expected, 5) ||
            ambient is null || projected is null ||
            Enumerable.Range(0, 4).Any(i =>
                Math.Sqrt(Math.Pow(ambient.Corners[i].X - projected.Corners[i].X, 2) +
                    Math.Pow(ambient.Corners[i].Y - projected.Corners[i].Y, 2)) > 3) ||
            originalBoard.Corners[0].X - ambient.Corners[0].X < 15)
            throw new Exception($"Moved-board corner refinement failed: " +
                $"ambient={Format(ambient)}; projected={Format(projected)}");
        Console.WriteLine($"Moved-board hardware regression: ambient={Format(ambient)}; " +
            $"white={Format(projected)}; all corners agree within 3 px");

        // Drop each real edge in turn. The recovery is then allowed to inspect only
        // the other three sides of the moved camera frame, with the earlier board
        // pose supplying the missing fixed-size plane constraint.
        for (int missing = 0; missing < 4; missing++)
        {
            BoardDetection? recovered = BoardDetector.RecoverAmbientBoardWithPrior(
                width, height, stride, black, originalBoard, 0b1111 & ~(1 << missing));
            if (recovered is null || Enumerable.Range(0, 4).Any(corner =>
                Math.Sqrt(Math.Pow(recovered.Corners[corner].X - ambient.Corners[corner].X, 2) +
                    Math.Pow(recovered.Corners[corner].Y - ambient.Corners[corner].Y, 2)) > 6))
                throw new Exception($"Three-edge moved-board recovery failed with side " +
                    $"{missing} hidden: recovered={Format(recovered)}, complete={Format(ambient)}");
            Console.WriteLine($"Three-edge moved-board regression: side {missing} hidden; " +
                $"recovered={Format(recovered)}");
        }


        // Erase each photographed side with a gradual interpolation across its
        // normal. The automatic path must identify the surviving three lines in
        // real shadowed camera pixels rather than receive an explicit side mask.
        byte[] ErasePhotographedSide(int side)
        {
            byte[] obscured = (byte[])black.Clone();
            PixelPoint start = ambient.Corners[side];
            PixelPoint end = ambient.Corners[(side + 1) % 4];
            bool horizontal = Math.Abs(end.X - start.X) > Math.Abs(end.Y - start.Y);
            double alongStart = horizontal ? start.X : start.Y;
            double alongEnd = horizontal ? end.X : end.Y;
            int min = (int)Math.Ceiling(Math.Min(alongStart, alongEnd) +
                Math.Abs(alongEnd - alongStart) * 0.04);
            int max = (int)Math.Floor(Math.Max(alongStart, alongEnd) -
                Math.Abs(alongEnd - alongStart) * 0.04);
            for (int along = min; along <= max; along++)
            {
                double progress = (along - alongStart) / (alongEnd - alongStart);
                int center = (int)Math.Round(horizontal
                    ? start.Y + progress * (end.Y - start.Y)
                    : start.X + progress * (end.X - start.X));
                int first = center - 58, last = center + 58;
                for (int across = first + 1; across < last; across++)
                {
                    double t = (across - first) / (double)(last - first);
                    for (int channel = 0; channel < 3; channel++)
                    {
                        int firstIndex = horizontal
                            ? first * stride + along * 4 + channel
                            : along * stride + first * 4 + channel;
                        int lastIndex = horizontal
                            ? last * stride + along * 4 + channel
                            : along * stride + last * 4 + channel;
                        int targetIndex = horizontal
                            ? across * stride + along * 4 + channel
                            : along * stride + across * 4 + channel;
                        obscured[targetIndex] = (byte)Math.Clamp((int)Math.Round(
                            black[firstIndex] * (1 - t) + black[lastIndex] * t), 0, 255);
                    }
                }
            }
            return obscured;
        }
        for (int side = 0; side < 4; side++)
        {
            BoardDetection? recovered = BoardDetector.RecoverAmbientBoardWithPrior(
                width, height, stride, ErasePhotographedSide(side), originalBoard);
            if (recovered is null || Enumerable.Range(0, 4).Any(corner =>
                Math.Sqrt(Math.Pow(recovered.Corners[corner].X - ambient.Corners[corner].X, 2) +
                    Math.Pow(recovered.Corners[corner].Y - ambient.Corners[corner].Y, 2)) > 6))
                throw new Exception($"Obscured real edge {side} was not recovered: " +
                    $"{Format(recovered)}");
            Console.WriteLine($"Obscured photographed edge {side} regression: {Format(recovered)}");
        }

        byte[] noEdges = new byte[black.Length];
        if (BoardDetector.RecoverAmbientBoardWithPrior(width, height, stride,
            noEdges, originalBoard) is not null)
            throw new Exception("Three-edge recovery hallucinated cardboard in a blank frame.");
        BoardDetection unrelatedPose = new(
            originalBoard.Corners.Select(point => new PixelPoint(point.X + 150, point.Y)).ToArray(),
            originalBoard.Confidence);
        if (BoardDetector.RecoverAmbientBoardWithPrior(width, height, stride,
            black, unrelatedPose) is not null)
            throw new Exception("Three-edge recovery accepted a distant prior board pose.");
    }

    internal static void CheckCalibrationSpot()
    {
        const int sceneWidth = 960, sceneHeight = 540;
        using Mat white = new(sceneHeight, sceneWidth, MatType.CV_8UC4,
            new Scalar(225, 218, 208, 255));
        using Mat spot = white.Clone();
        Point center = new(638, 197);
        Cv2.Circle(spot, center, 22, new Scalar(45, 43, 40, 255), -1);
        CalibrationSpotDetection? found = BoardDetector.DetectDarkCalibrationSpot(
            sceneWidth, sceneHeight, sceneWidth * 4, BytesOf(white), BytesOf(spot));
        if (found is null || Math.Abs(found.Center.X - center.X) > 4 ||
            Math.Abs(found.Center.Y - center.Y) > 4 || found.RadiusPixels < 12)
            throw new Exception($"Dark calibration spot failed: {found}");
        if (BoardDetector.DetectDarkCalibrationSpot(sceneWidth, sceneHeight, sceneWidth * 4,
            BytesOf(white), BytesOf(white)) is not null)
            throw new Exception("Unchanged frames produced a false calibration spot.");
        using Mat shiftedExposure = spot.Clone();
        shiftedExposure.ConvertTo(shiftedExposure, MatType.CV_8UC4, 1, -12);
        CalibrationSpotDetection? afterExposureShift = BoardDetector.DetectDarkCalibrationSpot(
            sceneWidth, sceneHeight, sceneWidth * 4, BytesOf(white), BytesOf(shiftedExposure));
        if (afterExposureShift is null || Math.Abs(afterExposureShift.Center.X - center.X) > 4 ||
            Math.Abs(afterExposureShift.Center.Y - center.Y) > 4)
            throw new Exception("Calibration spot was lost after global exposure shift.");
        Console.WriteLine("Dark-spot regression: camera centroid within 4 px, exposure shift tolerated; " +
            "unchanged frame rejected");
    }

    internal static bool Near(BoardDetection? actual, Point[] expected, double tolerance) =>
        actual is not null && actual.Corners.Length == 4 &&
        Enumerable.Range(0, 4).All(i =>
            Math.Sqrt(Math.Pow(actual.Corners[i].X - expected[i].X, 2) +
                Math.Pow(actual.Corners[i].Y - expected[i].Y, 2)) <= tolerance);

    internal static void CheckBoardDetection()
    {
        const int boardWidth = 640, boardHeight = 480;
        using Mat image = new(boardHeight, boardWidth, MatType.CV_8UC4,
            new Scalar(25, 28, 35, 255));
        Point[] board = [new(105, 48), new(520, 62), new(502, 427), new(90, 411)];
        Point[] grid = [new(154, 89), new(470, 98), new(454, 371), new(137, 359)];
        Cv2.FillConvexPoly(image, board, new Scalar(165, 170, 172, 255));
        Cv2.FillConvexPoly(image, grid, new Scalar(180, 115, 75, 255));
        for (int i = 1; i < 7; i++)
        {
            double t = i / 7.0;
            Point left = Lerp(grid[0], grid[3], t), right = Lerp(grid[1], grid[2], t);
            Point top = Lerp(grid[0], grid[1], t), bottom = Lerp(grid[3], grid[2], t);
            Cv2.Line(image, left, right, new Scalar(70, 60, 50, 255), 1);
            Cv2.Line(image, top, bottom, new Scalar(70, 60, 50, 255), 1);
        }
        BoardDetection? found = BoardDetector.Detect(boardWidth, boardHeight,
            boardWidth * 4, BytesOf(image));
        if (found is null || found.Corners.Length != 4 ||
            Enumerable.Range(0, 4).Any(i =>
                Math.Sqrt(Math.Pow(found.Corners[i].X - board[i].X, 2) +
                    Math.Pow(found.Corners[i].Y - board[i].Y, 2)) > 12))
            throw new Exception("Projected-grid board corner regression failed.");

        using Mat noBoard = new(boardHeight, boardWidth, MatType.CV_8UC4,
            new Scalar(25, 28, 35, 255));
        Cv2.FillConvexPoly(noBoard, grid, new Scalar(180, 115, 75, 255));
        if (BoardDetector.Detect(boardWidth, boardHeight, boardWidth * 4,
            BytesOf(noBoard)) is not null)
            throw new Exception("Projection alone was misidentified as the white board.");

        Console.WriteLine($"Board regression: four corners within 12 px; confidence={found.Confidence:F2}; " +
            "projection-only frame rejected");
    }

    internal static void CheckProjectorFieldIsNotBoard()
    {
        const int sceneWidth = 960, sceneHeight = 540;
        using Mat image = new(sceneHeight, sceneWidth, MatType.CV_8UC4,
            new Scalar(29, 24, 28, 255));
        Point[] projectorField = [new(90, 50), new(870, 50), new(870, 490), new(90, 490)];
        Point[] squareGrid = [new(281, 65), new(680, 65), new(680, 465), new(281, 465)];
        Cv2.FillConvexPoly(image, projectorField, new Scalar(160, 65, 30, 255));
        Cv2.FillConvexPoly(image, squareGrid, new Scalar(255, 202, 201, 255));
        if (BoardDetector.Detect(sceneWidth, sceneHeight, sceneWidth * 4,
            BytesOf(image)) is not null)
            throw new Exception("Projector's 16:9 light field was misidentified as the square board.");
        Console.WriteLine("Board regression: 16:9 projector field around square grid rejected");
    }

    internal static Point Lerp(Point start, Point end, double t) => new(
        (int)Math.Round(start.X + (end.X - start.X) * t),
        (int)Math.Round(start.Y + (end.Y - start.Y) * t));

    internal static byte[] BytesOf(Mat image)
    {
        int size = checked(image.Width * image.Height * 4);
        byte[] data = new byte[size];
        Marshal.Copy(image.Data, data, 0, size);
        return data;
    }
}
