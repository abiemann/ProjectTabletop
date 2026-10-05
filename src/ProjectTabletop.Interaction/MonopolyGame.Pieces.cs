namespace ProjectTabletop.Interaction;

public sealed partial class MonopolyGame
{
    /// <summary>Stable save/atlas order. Silver pieces are independent of ownership colours.</summary>
    public static IReadOnlyList<string> PieceNames { get; } = Array.AsReadOnly(new[]
        { "Hat", "Car", "Shoe", "Dog", "Gun", "Iron", "Wheelbarrow", "Steamship" });

    // Retain inactive slot preferences. Reopening a slot reconciles any choice
    // taken while it was absent, without changing another active player's piece.
    private readonly int[] _setupPieces = [0, 1, 2, 3, 4, 5];

    private void CycleSetupPiece(int slot)
    {
        var occupied = _setupPieces.Take(_state.Humans + _state.Ais)
            .Where((_, index) => index != slot).ToHashSet();
        int next = (_setupPieces[slot] + 1) % PieceNames.Count;
        while (occupied.Contains(next)) next = (next + 1) % PieceNames.Count;
        _setupPieces[slot] = next;
    }

    private void ReconcileSetupPieces()
    {
        var occupied = new HashSet<int>();
        for (int slot = 0; slot < _state.Humans + _state.Ais; slot++)
        {
            int piece = _setupPieces[slot];
            while (occupied.Contains(piece)) piece = (piece + 1) % PieceNames.Count;
            _setupPieces[slot] = piece;
            occupied.Add(piece);
        }
    }

    private static void ResolveSavedPieces(MonopolySaveData state)
    {
        var occupied = new HashSet<int>();
        // Explicit choices are authoritative. Reject duplicates before resolving
        // missing legacy fields, rather than silently changing a saved design.
        foreach (var player in state.Players)
            if (player.PieceIndex is { } piece && (piece < 0 || piece >= PieceNames.Count || !occupied.Add(piece)))
                throw new FormatException("The Crown & Deed save contains invalid or duplicate pieces.");
        foreach (var player in state.Players.Where(player => player.PieceIndex is null))
        {
            int piece = player.ColorIndex;
            while (occupied.Contains(piece)) piece = (piece + 1) % PieceNames.Count;
            player.PieceIndex = piece;
            occupied.Add(piece);
        }
    }
}
