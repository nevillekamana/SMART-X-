namespace SmartX.Services.Mesh;

/// <summary>
/// A self-balancing binary search tree (AVL), written from scratch — no SortedSet / SortedDictionary underneath.
///
/// Rule: for every node, |height(left) − height(right)| ≤ 1. After each Insert / Remove the path back to the root is
/// re-checked and one of four rotations restores the rule (left, right, left-right, right-left). That keeps
/// the height at ≤ 1.44·log₂(n+2), so Insert, Remove and lookup are O(log n) even when keys arrive already sorted
/// (which is exactly what time-stamped telemetry does — a plain BST degenerates into a linked list there).
///
/// Range queries (<see cref="VisitRange"/>) descend only into sub-trees that can contain keys inside [from, to], so a
/// query costs O(log n + k) where k is the number of matches.
/// </summary>
public class AvlTree<TKey, TValue>
{
    private sealed class Node
    {
        public TKey Key;
        public TValue Value;
        public Node? Left, Right;
        public int Height = 1;
        public Node(TKey key, TValue value) { Key = key; Value = value; }
    }

    private readonly IComparer<TKey> _cmp;
    private Node? _root;

    public AvlTree(IComparer<TKey>? comparer = null) { _cmp = comparer ?? Comparer<TKey>.Default; }

    public int Count { get; private set; }
    /// <summary>Number of rotations performed since the tree was created (each is one rebalancing step).</summary>
    public long Rotations { get; private set; }
    public int Height => H(_root);

    private static int H(Node? n) => n?.Height ?? 0;
    private static void Update(Node n) => n.Height = 1 + Math.Max(H(n.Left), H(n.Right));
    private static int Balance(Node? n) => n is null ? 0 : H(n.Left) - H(n.Right);

    // ─── Rotations ──────────────────────────────────────────────────
    //        y                x
    //       / \              / \
    //      x   C   ──►      A   y        RotateRight(y)  (left-left case)
    //     / \                  / \
    //    A   B                B   C
    private Node RotateRight(Node y)
    {
        var x = y.Left!;
        y.Left = x.Right;
        x.Right = y;
        Update(y); Update(x);
        Rotations++;
        return x;
    }

    private Node RotateLeft(Node x)                     // mirror image (right-right case)
    {
        var y = x.Right!;
        x.Right = y.Left;
        y.Left = x;
        Update(x); Update(y);
        Rotations++;
        return y;
    }

    /// <summary>Restore the AVL rule at <paramref name="n"/> after one of its sub-trees changed height.</summary>
    private Node Rebalance(Node n)
    {
        Update(n);
        int b = Balance(n);
        if (b > 1)                                       // left-heavy
        {
            if (Balance(n.Left) < 0) n.Left = RotateLeft(n.Left!);   // left-right: rotate the child first
            return RotateRight(n);                                    // left-left
        }
        if (b < -1)                                      // right-heavy
        {
            if (Balance(n.Right) > 0) n.Right = RotateRight(n.Right!); // right-left
            return RotateLeft(n);                                      // right-right
        }
        return n;
    }

    // ─── Insert ─────────────────────────────────────────────────────
    /// <summary>Insert a key. If it already exists its value is replaced and false is returned.</summary>
    public bool Insert(TKey key, TValue value)
    {
        bool added = false;
        _root = Insert(_root, key, value, ref added);
        if (added) Count++;
        return added;
    }

    private Node Insert(Node? n, TKey key, TValue value, ref bool added)
    {
        if (n is null) { added = true; return new Node(key, value); }
        int c = _cmp.Compare(key, n.Key);
        if (c < 0) n.Left = Insert(n.Left, key, value, ref added);
        else if (c > 0) n.Right = Insert(n.Right, key, value, ref added);
        else { n.Value = value; return n; }
        return Rebalance(n);
    }

    // ─── Remove ─────────────────────────────────────────────────────
    public bool Remove(TKey key)
    {
        bool removed = false;
        _root = Remove(_root, key, ref removed);
        if (removed) Count--;
        return removed;
    }

    private Node? Remove(Node? n, TKey key, ref bool removed)
    {
        if (n is null) return null;
        int c = _cmp.Compare(key, n.Key);
        if (c < 0) n.Left = Remove(n.Left, key, ref removed);
        else if (c > 0) n.Right = Remove(n.Right, key, ref removed);
        else
        {
            removed = true;
            if (n.Left is null) return n.Right;          // 0 or 1 child: splice it out
            if (n.Right is null) return n.Left;
            var succ = n.Right;                          // 2 children: copy the in-order successor up, then delete it
            while (succ.Left is not null) succ = succ.Left;
            n.Key = succ.Key; n.Value = succ.Value;
            bool dummy = false;
            n.Right = Remove(n.Right, succ.Key, ref dummy);
        }
        return Rebalance(n);
    }

    // ─── Lookups ────────────────────────────────────────────────────
    public bool TryGetValue(TKey key, out TValue value)
    {
        var n = _root;
        while (n is not null)
        {
            int c = _cmp.Compare(key, n.Key);
            if (c == 0) { value = n.Value; return true; }
            n = c < 0 ? n.Left : n.Right;
        }
        value = default!;
        return false;
    }

    public bool TryGetMin(out TKey key, out TValue value)
    {
        var n = _root;
        if (n is null) { key = default!; value = default!; return false; }
        while (n.Left is not null) n = n.Left;
        key = n.Key; value = n.Value; return true;
    }

    public bool TryGetMax(out TKey key, out TValue value)
    {
        var n = _root;
        if (n is null) { key = default!; value = default!; return false; }
        while (n.Right is not null) n = n.Right;
        key = n.Key; value = n.Value; return true;
    }

    // ─── Range query ────────────────────────────────────────────────
    /// <summary>
    /// Calls <paramref name="visit"/> for every entry with from ≤ key ≤ to, in ascending key order, and returns how many
    /// tree nodes were looked at. Sub-trees that cannot hold a key in range are never entered (pruned):
    ///   • key &lt; from  → everything in the left sub-tree is smaller still, so skip it
    ///   • key &gt; to    → everything in the right sub-tree is larger still, so skip it
    /// </summary>
    public int VisitRange(TKey from, TKey to, Action<TKey, TValue> visit)
    {
        if (_cmp.Compare(from, to) > 0) return 0;
        int visited = 0;
        VisitRange(_root, from, to, visit, ref visited);
        return visited;
    }

    private void VisitRange(Node? n, TKey from, TKey to, Action<TKey, TValue> visit, ref int visited)
    {
        if (n is null) return;
        visited++;
        int cFrom = _cmp.Compare(n.Key, from);
        int cTo = _cmp.Compare(n.Key, to);
        if (cFrom > 0) VisitRange(n.Left, from, to, visit, ref visited);     // left may still hold keys ≥ from
        if (cFrom >= 0 && cTo <= 0) visit(n.Key, n.Value);
        if (cTo < 0) VisitRange(n.Right, from, to, visit, ref visited);      // right may still hold keys ≤ to
    }

    // ─── Traversal + self-check ─────────────────────────────────────
    /// <summary>All entries in ascending key order (iterative, so it never recurses deeply).</summary>
    public IEnumerable<KeyValuePair<TKey, TValue>> InOrder()
    {
        var stack = new Stack<Node>();
        var n = _root;
        while (n is not null || stack.Count > 0)
        {
            while (n is not null) { stack.Push(n); n = n.Left; }
            n = stack.Pop();
            yield return new KeyValuePair<TKey, TValue>(n.Key, n.Value);
            n = n.Right;
        }
    }

    /// <summary>
    /// Verifies the three invariants: (1) strict BST ordering, (2) every stored height equals the real height,
    /// (3) |balance factor| ≤ 1 everywhere. Used by the tests after thousands of inserts and removals.
    /// </summary>
    public bool IsValid() => Check(_root, default, false, default, false, out _);

    private bool Check(Node? n, TKey? lo, bool hasLo, TKey? hi, bool hasHi, out int height)
    {
        if (n is null) { height = 0; return true; }
        if (hasLo && _cmp.Compare(n.Key, lo!) <= 0) { height = 0; return false; }
        if (hasHi && _cmp.Compare(n.Key, hi!) >= 0) { height = 0; return false; }
        if (!Check(n.Left, lo, hasLo, n.Key, true, out int hl)) { height = 0; return false; }
        if (!Check(n.Right, n.Key, true, hi, hasHi, out int hr)) { height = 0; return false; }
        height = 1 + Math.Max(hl, hr);
        return n.Height == height && Math.Abs(hl - hr) <= 1;
    }
}
