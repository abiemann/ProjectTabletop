// The suites were written to run one group at a time: some share process-wide
// counters, temporary profile folders and OpenCV threads. Keep that ordering.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

// Each group prints a one-line summary; show it in that test's output.
[assembly: CaptureConsole]
