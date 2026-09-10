namespace DnDEz.Rules;

/// <summary>
/// คิดราคาการเคลื่อนที่บนกริด (spatial-play-design §3.1–§3.3) — เลขคณิตล้วน ไม่มี RNG เข้ามาเกี่ยวเลย
/// จึง **ไม่รับ <see cref="IDiceRoller"/>** ต่างจาก resolver อื่นในโฟลเดอร์นี้: การกระโดดลงพื้นเดินยาก
/// ต้องทอย DC 10 ก็จริง แต่ engine แค่ "ยกธง" ให้ชั้นบนไปทอยเอง (ดู
/// <see cref="PathCost.RequiresLandingCheck"/>) เพราะการทอยไม่ได้เปลี่ยนราคา
///
/// ตารางราคาต่อ "ช่องที่เข้า" (1 ช่อง = <see cref="FeetPerSquare"/> ฟุต) อ้าง SRD 5.2 ฉบับ CC:
/// <list type="table">
///   <listheader><term>โหมด</term><description>พื้นปกติ / พื้นเดินยาก</description></listheader>
///   <item><term>Walk</term><description>5 / 10</description></item>
///   <item><term>Jump</term><description>5 / 5</description></item>
///   <item><term>Fly</term><description>5 / 5</description></item>
///   <item><term>Climb (ไม่มี Climb Speed)</term><description>10 / 15</description></item>
///   <item><term>Climb (มี Climb Speed)</term><description>5 / 10</description></item>
/// </list>
///
/// ระยะบนกริดเป็นแบบ Chebyshev — SRD 5.2: *"It costs 1 square of movement to enter an unoccupied
/// square that's adjacent to your space (orthogonally or diagonally adjacent)"* → **ทแยงราคาเท่าตรง**
/// กฎสลับ 5/10 ฟุตต่อทแยงเป็น optional variant ที่ไม่ได้อยู่ใน SRD ห้ามเผลอใส่
/// </summary>
public static class MovementCostResolver
{
    /// <summary>หนึ่งช่องบนกริด = 5 ฟุต (SRD 5.2 กริด: "30 feet translates into 6 squares")</summary>
    public const int FeetPerSquare = 5;

    /// <summary>DC ของการทรงตัวตอนลงพื้นเดินยากหลังกระโดด — SRD 5.2 "Long Jump": *"If you land in
    /// Difficult Terrain, you must succeed on a DC 10 Dexterity (Acrobatics) check or have the
    /// Prone condition."* · engine ไม่ทอยเอง แค่บอกว่าต้องทอย</summary>
    public const int DifficultLandingDc = 10;

    /// <summary>
    /// คิดราคาของเส้นทางที่ผู้เรียกกำหนดมาแล้ว
    ///
    /// **ราคาคิดตอน "เข้า" ช่อง** ช่องแรกของเส้นทางคือช่องที่ยืนอยู่แล้วจึงไม่เคยถูกคิดเงิน (ไม่งั้น
    /// คนที่ยืนอยู่ในหนองน้ำจะโดนชาร์จฟรีทุกครั้งที่ขยับ) เส้นทางที่มีช่องเดียว = ไม่ขยับ = 0 ฟุต
    ///
    /// input ที่ผิดสัญญาถือเป็น caller bug โยน <see cref="ArgumentException"/> ทันทีตามแพทเทิร์นเดียว
    /// กับ resolver อื่นในโฟลเดอร์นี้ ไม่ใช่กลืนเงียบแล้วคืนเลขมั่ว
    /// </summary>
    /// <exception cref="ArgumentException">
    /// เส้นทางว่าง, มีคู่ช่องที่ไม่ติดกัน (ระยะ Chebyshev ไม่เท่ากับ 1 — ระยะ 0 คือย่ำช่องซ้ำก็ผิด),
    /// เข้าช่องที่ถูกปิดกั้น (ทุกโหมดรวมทั้งบิน), หรือเดินทแยงเฉียดมุมกำแพง
    /// </exception>
    public static PathCost CostOfPath(
        IReadOnlyList<GridPoint> path,
        MovementMode mode,
        TerrainField terrain,
        MovementProfile profile)
    {
        if (path.Count == 0)
        {
            throw new ArgumentException("เส้นทางห้ามว่าง", nameof(path));
        }

        var totalFeet = 0;

        for (var i = 1; i < path.Count; i++)
        {
            var from = path[i - 1];
            var to = path[i];
            var chebyshev = Math.Max(Math.Abs(to.X - from.X), Math.Abs(to.Y - from.Y));

            if (chebyshev != 1)
            {
                throw new ArgumentException(
                    $"ช่อง {from} กับ {to} ไม่ติดกัน (ระยะ Chebyshev ต้องเป็น 1 พอดี ระยะ 0 คือย่ำช่องซ้ำก็ผิด)",
                    nameof(path));
            }

            if (IsStepBlocked(from, to, terrain))
            {
                throw new ArgumentException(
                    $"เข้าช่อง {to} จาก {from} ไม่ได้ — ถูกปิดกั้นตรง ๆ หรือเดินทแยงเฉียดมุมกำแพง",
                    nameof(path));
            }

            totalFeet += EnterCost(mode, terrain.IsDifficult(to), profile.HasClimbSpeed);
        }

        var isSingleSquare = path.Count == 1;

        // path ช่องเดียว = ไม่ขยับ = affordable เสมอ แม้ immobilized เพราะยืนเฉยไม่ใช่การเคลื่อนที่
        var isAffordable = isSingleSquare || (!profile.IsImmobilized && totalFeet <= profile.BudgetFeet);

        // ต้องทอยลงพื้นเฉพาะกระโดดแล้วไปจบที่พื้นเดินยาก — ไม่เกี่ยวกับราคา แค่ยกธงให้ชั้นบนไปทอย
        var requiresLandingCheck =
            !isSingleSquare && mode == MovementMode.Jump && terrain.IsDifficult(path[^1]);

        return new PathCost(totalFeet, isAffordable, requiresLandingCheck);
    }

    /// <summary>
    /// "เดินได้ถึงไหน" — กระจายตามต้นทุนจริงด้วย Dijkstra (uniform-cost search) ไม่ใช่วงรัศมี
    ///
    /// §3.1 ระบุว่า *"ถ้ามีพื้นเดินยากขวางอยู่ ช่องที่ไกลเท่ากันจะไปถึงได้ไม่เท่ากัน และบางทีอ้อมไกลกว่า
    /// กลับถูกกว่าลุยตรง — ระบบต้องหาทางที่ถูกที่สุดให้เอง"* ดังนั้น BFS นับจำนวนก้าว หรือสูตร
    /// "ระยะ × ตัวคูณพื้น" ใช้ไม่ได้ เพราะราคาต่อช่องไม่เท่ากัน (5/10/15)
    /// </summary>
    /// <returns>ช่องที่ไปถึงได้ทั้งหมด (รวมช่องเริ่มต้นที่ราคา 0 เสมอ) พร้อมราคาที่ถูกที่สุดและเส้นทาง
    /// ที่ทำราคานั้นได้จริง — เฉพาะช่องที่ราคาไม่เกิน <see cref="MovementProfile.BudgetFeet"/></returns>
    public static IReadOnlyDictionary<GridPoint, ReachableSquare> Reachable(
        GridPoint origin,
        MovementMode mode,
        TerrainField terrain,
        MovementProfile profile)
    {
        var best = new Dictionary<GridPoint, ReachableSquare>
        {
            [origin] = new ReachableSquare(origin, 0, new[] { origin })
        };

        if (profile.IsImmobilized)
        {
            return best;
        }

        // frontier ของ Dijkstra เรียงด้วย (ราคา, ลำดับที่ถูก enqueue) — ต้องมี tie-break ที่ deterministic
        // เอง (Seq ที่นับขึ้นเรื่อย ๆ ตามลำดับ neighbour คงที่) ห้ามให้ HashSet/Dictionary enumeration
        // order ของ .NET หลุดเข้ามาเป็นตัวตัดสินว่าใครถูก dequeue ก่อนตอนราคาเท่ากัน · เก็บคีย์ไว้แค่
        // (Cost, Seq) เพราะ GridPoint ไม่ใช่ IComparable — ถ้าเอา GridPoint ไปแทรกในทูเพิลที่เรียงลำดับ
        // ด้วย SortedSet มันจะโยน ArgumentException ตอนราคา+ลำดับเท่ากันแล้วต้องเทียบตัวที่สาม (ไม่เกิดจริง
        // เพราะ Seq unique แต่ .NET เรียก Comparer ไว้ก่อนโดยไม่รู้ล่วงหน้า) จึงแยก point ไปเก็บใน
        // dictionary ต่างหากตาม seq แทน
        var frontier = new SortedSet<(int Cost, long Seq)>();
        var pointBySeq = new Dictionary<long, GridPoint>();
        var seq = 0L;
        frontier.Add((0, seq));
        pointBySeq[seq++] = origin;

        while (frontier.Count > 0)
        {
            var currentEntry = frontier.Min;
            frontier.Remove(currentEntry);
            var (cost, currentSeq) = currentEntry;
            var point = pointBySeq[currentSeq];

            // entry ล้าสมัย (เคย enqueue ไว้ตอนยังไม่รู้ราคาที่ดีที่สุดของช่องนี้) ข้ามทิ้งแบบ lazy deletion
            if (!best.TryGetValue(point, out var recorded) || recorded.CostFeet != cost)
            {
                continue;
            }

            foreach (var (dx, dy) in Neighbours)
            {
                var next = new GridPoint(point.X + dx, point.Y + dy);

                if (IsStepBlocked(point, next, terrain))
                {
                    continue;
                }

                var newCost = cost + EnterCost(mode, terrain.IsDifficult(next), profile.HasClimbSpeed);

                // bound ด้วยงบแทนขอบแผนที่ (กริดไม่มีขอบเขต) — ราคาต่อก้าวไม่เคยเป็น 0 จึงการันตีว่า
                // ลูปนี้จบเสมอ ไม่แผ่ไปไม่มีที่สิ้นสุด
                if (newCost > profile.BudgetFeet)
                {
                    continue;
                }

                if (best.TryGetValue(next, out var existing) && existing.CostFeet <= newCost)
                {
                    continue;
                }

                var newPath = new List<GridPoint>(recorded.Path.Count + 1);
                newPath.AddRange(recorded.Path);
                newPath.Add(next);

                best[next] = new ReachableSquare(next, newCost, newPath);
                pointBySeq[seq] = next;
                frontier.Add((newCost, seq++));
            }
        }

        return best;
    }

    /// <summary>ทิศทั้ง 8 ตามลำดับคงที่ — ใช้เป็น tie-break ลำดับที่สองของ Dijkstra เพื่อให้ผลลัพธ์
    /// เหมือนกันทุกครั้งที่รันแม้มีหลายเส้นทางราคาเท่ากัน</summary>
    private static readonly (int Dx, int Dy)[] Neighbours =
    {
        (-1, -1), (0, -1), (1, -1),
        (-1, 0), (1, 0),
        (-1, 1), (0, 1), (1, 1)
    };

    /// <summary>
    /// เช็คว่าก้าวจาก from ไป to (ที่ติดกันอยู่แล้ว ระยะ Chebyshev = 1) เข้าไม่ได้เพราะอะไร — ทั้งช่อง
    /// ปลายทางถูกปิดกั้นตรง ๆ หรือเดินทแยงเฉียดมุมกำแพง (SRD 5.2 "Corners")
    ///
    /// ต้องเป็น helper กลางให้ <see cref="CostOfPath"/> และ <see cref="Reachable"/> เรียกร่วมกันเท่านั้น
    /// ห้ามลอกกฎไปเขียนซ้ำสองที่ — ถ้าแก้กฎมุมกำแพงแล้วลืมแก้อีกฟังก์ชัน จะเกิดบั๊กที่ผู้เล่นเห็นไฮไลต์ว่า
    /// เดินอ้อมมุมได้ (จาก Reachable) แต่กดจริงกลับโดน CostOfPath โยน exception
    /// </summary>
    private static bool IsStepBlocked(GridPoint from, GridPoint to, TerrainField terrain)
    {
        if (terrain.IsBlocked(to))
        {
            return true;
        }

        var dx = to.X - from.X;
        var dy = to.Y - from.Y;

        if (dx == 0 || dy == 0)
        {
            return false;
        }

        // ก้าวทแยง — เฉียดมุมไม่ได้ถ้าช่องข้างเคียงฝั่งใดฝั่งหนึ่ง (แนวนอนหรือแนวตั้งของก้าวนี้) ถูกปิดกั้น
        var corner1 = new GridPoint(from.X + dx, from.Y);
        var corner2 = new GridPoint(from.X, from.Y + dy);
        return terrain.IsBlocked(corner1) || terrain.IsBlocked(corner2);
    }

    /// <summary>
    /// ราคาต่อ "ช่องที่เข้า" หนึ่งช่อง ตามตารางในหัว doc comment ของคลาสนี้ — แยกเป็น helper เดียวกลาง
    /// เพราะทั้ง CostOfPath (เดินตาม path ที่กำหนด) และ Reachable (แผ่ทีละช่องด้วย Dijkstra) ต้องใช้
    /// สูตรราคาเดียวกันเป๊ะ ไม่งั้นสองฟังก์ชันจะรายงานราคาขัดกันเอง (ดูเทสต์
    /// Reachable_EveryReturnedPath_CostsExactlyWhatWasReported)
    /// </summary>
    private static int EnterCost(MovementMode mode, bool isDifficult, bool hasClimbSpeed) => mode switch
    {
        MovementMode.Jump or MovementMode.Fly => FeetPerSquare,
        MovementMode.Climb when hasClimbSpeed => isDifficult ? FeetPerSquare * 2 : FeetPerSquare,
        MovementMode.Climb => isDifficult ? FeetPerSquare * 3 : FeetPerSquare * 2,
        MovementMode.Walk => isDifficult ? FeetPerSquare * 2 : FeetPerSquare,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "โหมดการเคลื่อนที่ไม่รู้จัก")
    };
}
