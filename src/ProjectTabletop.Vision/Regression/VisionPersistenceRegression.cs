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

            File.Delete(classifierPath);
            CheckLoad("missing classifier recovery", expectedFits: 1);
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
            CheckLoad("incompatible native classifier dimensions", expectedFits: 1);
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
            "compatible loads fit no classifier; missing/stale classifiers fit once; untrained profiles remain untrained.");

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

        void CheckLoad(string scenario, int expectedFits)
        {
            using var loaded = VisionEngine.Load(directory);
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
