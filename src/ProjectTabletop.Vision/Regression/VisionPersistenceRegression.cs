using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenCvSharp;
using OpenCvSharp.ML;
using ProjectTabletop.Vision;

internal static class VisionPersistenceRegression
{
    // Uses the same enrolled pieces and unseen rotated/unknown evaluation frame
    // as the recognition regression. Full detections include identities, pose,
    // confidence, and fitted outlines, rather than testing cache metadata alone.
    internal static void Run(VisionEngine original, string directory, EvaluationFrame evaluation)
    {
        string profilePath = Path.Combine(directory, "vision-profile.json");
        string classifierPath = Path.Combine(directory, "vision-svm.yml");
        string profile = File.ReadAllText(profilePath);
        byte[] classifier = File.ReadAllBytes(classifierPath);
        string expected = Detections(original, evaluation);
        Require(original.ClassifierTrainingCount == 1, "The fixture did not fit exactly one classifier.");
        try
        {
            CheckLoad("compatible persisted classifier", expectedFits: 0);

            // A second save/load must retain the compatible cache, including its
            // normalization and numeric-label mapping.
            using (var roundTrip = VisionEngine.Load(directory)) roundTrip.Save(directory);
            CheckLoad("second save/load", expectedFits: 0);
            Restore();

            ChangeProfile(state => state.Remove("SvmSha256"));
            CheckLoad("legacy classifier without cache identity", expectedFits: 1);
            using (var migrated = VisionEngine.Load(directory)) migrated.Save(directory);
            CheckLoad("saved legacy profile has reusable cache identity", expectedFits: 0);
            Restore();

            File.Delete(classifierPath);
            CheckLoad("missing classifier recovery", expectedFits: 1);
            Restore();

            File.WriteAllBytes(classifierPath, classifier[..20]);
            CheckLoad("truncated classifier recovery", expectedFits: 1);
            Restore();

            File.WriteAllBytes(classifierPath, []);
            CheckLoad("empty classifier recovery", expectedFits: 1);
            Restore();

            File.WriteAllText(classifierPath, "%YAML:1.0\nopencv_ml_svm: [broken");
            BindCurrentClassifier(); // Reach the parser even with a matching digest.
            CheckLoad("malformed classifier recovery", expectedFits: 1);
            Restore();

            using (File.Open(classifierPath, FileMode.Open, FileAccess.Read, FileShare.None))
                CheckLoad("unreadable classifier recovery", expectedFits: 1);
            Restore();

            ChangeProfile(state =>
            {
                var labels = state["TrainedClassIds"]!.AsArray();
                state["TrainedClassIds"] = new JsonArray(labels.Reverse()
                    .Select(label => label!.DeepClone()).ToArray());
            });
            CheckLoad("changed numeric-label order", expectedFits: 1);
            Restore();

            ChangeProfile(state => state["FeatureMean"]![0] = state["FeatureMean"]![0]!.GetValue<double>() + 1);
            CheckLoad("different feature normalization", expectedFits: 1);
            Restore();

            ChangeProfile(state => state["FeatureDeviation"]!.AsArray().RemoveAt(0));
            CheckLoad("different descriptor dimensions", expectedFits: 1);
            Restore();

            WriteIncompatibleClassifier(classifierPath);
            BindCurrentClassifier();
            CheckLoad("incompatible native classifier dimensions", expectedFits: 1);
            Restore();

            CheckAtomicSave();
            Restore();

            CheckInterruptedTrainingSave();
            Restore();

            CheckInvalidCapture();
            Restore();

            using (var singleClass = VisionEngine.Load(directory))
            {
                foreach (var capture in singleClass.Captures.Where(capture => capture.PieceId == "B"))
                    singleClass.RemoveCapture(capture.CaptureId);
                singleClass.Train();
                string singleExpected = Detections(singleClass, evaluation);
                singleClass.Save(directory);
                using var singleLoaded = VisionEngine.Load(directory);
                Require(singleLoaded.IsTrained && singleLoaded.ClassifierTrainingCount == 0 &&
                    Detections(singleLoaded, evaluation) == singleExpected,
                    "A single-class profile changed its exemplar-only predictions or unnecessarily fitted a classifier.");
            }
            Restore();

            ChangeProfile(state =>
            {
                state["TrainedClassIds"] = null;
                state["FeatureMean"] = null;
                state["FeatureDeviation"] = null;
            });
            using var untrained = VisionEngine.Load(directory);
            Require(!untrained.IsTrained && untrained.ClassifierTrainingCount == 0 &&
                untrained.Detect(evaluation.Width, evaluation.Height, evaluation.Stride, evaluation.Bgra).Count == 0,
                "An untrained profile unexpectedly used or fitted the saved classifier.");
        }
        finally { Restore(); }
        Console.WriteLine("Vision persistence regression: unchanged recognition/pose/unknown rejection; " +
            "compatible loads fit no classifier; missing/damaged/stale classifiers fit once; " +
            "cache identity rejects mixed save generations; atomic replacement preserves previous profiles on failure; " +
            "untrained profiles remain untrained.");

        void Restore()
        {
            File.WriteAllText(profilePath, profile);
            File.WriteAllBytes(classifierPath, classifier);
        }

        void ChangeProfile(Action<JsonObject> change)
        {
            var state = JsonNode.Parse(profile)!.AsObject();
            change(state);
            File.WriteAllText(profilePath, state.ToJsonString());
        }

        void BindCurrentClassifier() => ChangeProfile(state => state["SvmSha256"] =
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(classifierPath))));

        void CheckInterruptedTrainingSave()
        {
            if (!OperatingSystem.IsWindows()) return;
            using var changed = VisionEngine.Load(directory);
            var first = JsonNode.Parse(profile)!["Captures"]![0]!;
            string sourcePath = Path.Combine(directory, "captures", original.Captures[0].CaptureId + ".png");
            using var image = Cv2.ImRead(sourcePath, ImreadModes.Unchanged);
            Require(image.Channels() == 4 && image.IsContinuous(), "The enrolled camera fixture is not contiguous BGRA.");
            byte[] pixels = new byte[image.Width * image.Height * 4];
            Marshal.Copy(image.Data, pixels, 0, pixels.Length);
            var outline = JsonSerializer.Deserialize<PixelPoint[]>(first["Outline"]!.ToJsonString())!;
            var front = JsonSerializer.Deserialize<PixelPoint>(first["Front"]!.ToJsonString());
            // Sorting a new class before A/B shifts both old numeric labels while
            // preserving the descriptor count and SVM type/kernel.
            CaptureInfo added = changed.AddLabeledCapture("0", image.Width, image.Height, image.Width * 4,
                pixels, outline, front);
            changed.Train();
            Require(changed.PieceIds.SequenceEqual(["0", "A", "B"]), "The interrupted-save fixture did not add a new class.");
            Require(Detections(changed, evaluation) != expected, "The changed classifier did not exercise different recognition.");
            try
            {
                using (File.Open(profilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    RequireSaveFailure("changed classifier with blocked profile publication", changed);
                Require(File.ReadAllText(profilePath) == profile &&
                    !File.ReadAllBytes(classifierPath).SequenceEqual(classifier),
                    "The interrupted save did not leave an older profile paired with the newer classifier cache.");
                using (var oldClassifier = SVM.LoadFromString(System.Text.Encoding.UTF8.GetString(classifier)))
                using (var newClassifier = SVM.Load(classifierPath))
                    Require(oldClassifier.GetVarCount() == newClassifier.GetVarCount() &&
                        oldClassifier.Type == newClassifier.Type && oldClassifier.KernelType == newClassifier.KernelType,
                        "The newer cache was trivially incompatible rather than a different training generation.");
                CheckLoad("old profile after interrupted changed-class save", expectedFits: 1);
                RequireNoTemporaryFiles();
                // Publishing the recovered old engine binds its reconstructed cache;
                // subsequent loads must use it without another classifier fit.
                using (var recovered = VisionEngine.Load(directory)) recovered.Save(directory);
                CheckLoad("repaired cache after interrupted save", expectedFits: 0);
            }
            finally
            {
                Restore();
                File.Delete(Path.Combine(directory, "captures", added.CaptureId + ".png"));
            }
        }

        void CheckAtomicSave()
        {
            int threshold = original.Settings.BrightnessThreshold;
            try
            {
                original.Settings.BrightnessThreshold = threshold + 1;
                // A reader of the previous profile must retain that complete file,
                // even while the new profile is published at the same pathname.
                using (var prior = new FileStream(profilePath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    original.Save(directory);
                    using var reader = new StreamReader(prior);
                    Require(reader.ReadToEnd() == profile,
                        "Saving overwrote the existing profile in place instead of atomically replacing it.");
                }
                using (var saved = VisionEngine.Load(directory))
                    Require(saved.Settings.BrightnessThreshold == threshold + 1 &&
                        saved.Captures.SequenceEqual(original.Captures) && saved.ClassifierTrainingCount == 0 &&
                        Detections(saved, evaluation) == Detections(original, evaluation),
                        "Atomic publication did not preserve the new settings, captures and compatible classifier.");
                RequireNoTemporaryFiles();
                Restore();

                if (OperatingSystem.IsWindows())
                {
                    // Allow writes but deny rename/delete. An in-place write would
                    // succeed and corrupt the old save; atomic replacement must fail.
                    using (File.Open(profilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        RequireSaveFailure("blocked profile publication");
                    Require(File.ReadAllText(profilePath) == profile,
                        "Failed profile publication changed the previous authoritative annotations.");
                    CheckLoad("previous profile after failed publication", expectedFits: 0);
                    RequireNoTemporaryFiles();
                    Restore();

                    using (File.Open(classifierPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        RequireSaveFailure("blocked classifier publication");
                    Require(File.ReadAllText(profilePath) == profile &&
                        File.ReadAllBytes(classifierPath).SequenceEqual(classifier),
                        "A failed classifier replacement changed the previous cache or published new annotations.");
                    CheckLoad("previous profile after failed cache publication", expectedFits: 0);
                    RequireNoTemporaryFiles();
                }
            }
            finally
            {
                original.Settings.BrightnessThreshold = threshold;
                Restore();
            }
        }

        void CheckInvalidCapture()
        {
            string capturePath = Path.Combine(directory, "captures", original.Captures[0].CaptureId + ".png");
            byte[] png = File.ReadAllBytes(capturePath);
            try
            {
                // Load creates the background before rebuilding exemplars. A bad
                // authoritative capture must still fail, with the partial engine
                // and its background released by the load-failure path.
                ChangeProfile(state => state["BackgroundPngBase64"] = Convert.ToBase64String(png));
                File.WriteAllBytes(capturePath, [1, 2, 3, 4]);
                bool rejected = false;
                try { using var invalid = VisionEngine.Load(directory); }
                catch (OpenCVException) { rejected = true; }
                Require(rejected, "Damaged authoritative capture pixels were silently accepted as an optional cache failure.");
            }
            finally
            {
                File.WriteAllBytes(capturePath, png);
                Restore();
            }
            CheckLoad("intact profile after a rejected capture", expectedFits: 0);
        }

        void RequireSaveFailure(string scenario, VisionEngine? source = null)
        {
            bool failed = false;
            try { (source ?? original).Save(directory); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failed = true; }
            Require(failed, $"{scenario}: the write unexpectedly succeeded.");
        }

        void RequireNoTemporaryFiles() => Require(
            Directory.GetFiles(directory, "*.tmp*", SearchOption.AllDirectories).Length == 0,
            "A completed or failed save left unpublished temporary files.");

        void CheckLoad(string scenario, int expectedFits)
        {
            using var loaded = VisionEngine.Load(directory);
            Require(loaded.Captures.SequenceEqual(original.Captures),
                $"{scenario}: intact saved captures were lost or changed.");
            Require(loaded.IsTrained && loaded.ClassifierTrainingCount == expectedFits,
                $"{scenario}: expected {expectedFits} classifier fits, got {loaded.ClassifierTrainingCount}.");
            Require(Detections(loaded, evaluation) == expected,
                $"{scenario}: persisted recognition, pose, or unknown rejection changed.");
        }
    }

    private static string Detections(VisionEngine engine, EvaluationFrame evaluation) =>
        JsonSerializer.Serialize(engine.Detect(evaluation.Width, evaluation.Height, evaluation.Stride, evaluation.Bgra));

    private static void WriteIncompatibleClassifier(string path)
    {
        using var samples = new Mat(4, 2, MatType.CV_32FC1);
        using var labels = new Mat(4, 1, MatType.CV_32SC1);
        for (int row = 0; row < 4; row++)
        {
            samples.Set(row, 0, (float)row);
            samples.Set(row, 1, (float)row);
            labels.Set(row, 0, row / 2);
        }
        using var svm = SVM.Create();
        svm.Type = SVM.Types.CSvc;
        svm.KernelType = SVM.KernelTypes.Rbf;
        svm.C = 2;
        svm.Gamma = .5;
        Require(svm.Train(samples, SampleTypes.RowSample, labels), "The incompatible-classifier fixture did not train.");
        svm.Save(path);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
