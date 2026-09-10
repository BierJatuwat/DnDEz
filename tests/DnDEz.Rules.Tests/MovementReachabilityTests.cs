namespace DnDEz.Rules.Tests;

/// <summary>
/// "เดินได้ถึงไหน" = การกระจายตามต้นทุน ไม่ใช่วงกลมรัศมี (spatial-play-design §3.1)
///
/// §3.1 เขียนไว้ตรง ๆ ว่า *"ถ้ามีพื้นเดินยากขวางอยู่ ช่องที่ไกลเท่ากันจะไปถึงได้ไม่เท่ากัน และบางที
/// อ้อมไกลกว่ากลับถูกกว่าลุยตรง — ระบบต้องหาทางที่ถูกที่สุดให้เอง"* และกำกับด้วยว่าข้อความเดิมใน
/// `player-side-design §4` ที่บอกให้วาด "วงรัศมี" นั้น **ใช้ไม่ได้** เมื่อมีพื้นเดินยาก
///
/// เทสต์สองตัวที่เป็นหัวใจของหัวข้อนี้คือ NotACircle และ DetourCheaperThanStraightLine —
/// implementation ที่คิดแบบ "ระยะทาง × ตัวคูณพื้น" หรือ BFS ที่ราคาเท่ากันทุกช่อง จะตกสองตัวนี้เสมอ
/// </summary>
public class MovementReachabilityTests
{
    private static GridPoint P(int x, int y) => new(x, y);

    private static TerrainField MapDifficult(params GridPoint[] squares) =>
        new(TerrainLayer.FromSquares(difficult: squares), TerrainLayer.Empty);

    /// <summary>กำแพงพื้นเดินยากขวางคอลัมน์ x=1 ตั้งแต่ y=-1 ถึง y=1 — บังคับให้ทุกเส้นทางที่จะข้าม
    /// ไปฝั่งขวาต้องจ่ายค่าพื้นเดินยาก หรืออ้อมขึ้น/ลงไกลกว่านั้น</summary>
    private static TerrainField DifficultColumnAtX1() =>
        MapDifficult(P(1, -1), P(1, 0), P(1, 1));

    [Fact]
    public void Reachable_ClearGround_ExpandsByCostIncludingDiagonals()
    {
        var reachable = MovementCostResolver.Reachable(
            P(0, 0), MovementMode.Walk, TerrainField.Empty, new MovementProfile(BudgetFeet: 10));

        // ช่องเริ่มต้นอยู่ในผลลัพธ์เสมอ ราคา 0 (ยืนอยู่เฉย ๆ ไม่เสียอะไร)
        Assert.Equal(0, reachable[P(0, 0)].CostFeet);

        // ทแยงราคาเท่าตรง → วงรอบแรก 8 ช่องที่ 5 ฟุต วงที่สอง 16 ช่องที่ 10 ฟุต + ช่องเริ่มต้น = 25
        Assert.Equal(25, reachable.Count);
        Assert.Equal(5, reachable[P(1, 1)].CostFeet);
        Assert.Equal(10, reachable[P(2, 2)].CostFeet);
        Assert.False(reachable.ContainsKey(P(3, 0)));
    }

    /// <summary>
    /// **หัวใจของ §3.1 ข้อแรก** — (0,2) กับ (2,0) อยู่ห่างจากจุดเริ่มเท่ากันเป๊ะ (2 ช่อง) แต่ด้วยงบ
    /// 10 ฟุตเท่ากัน ไปถึงได้แค่ (0,2) เพราะอีกทางมีพื้นเดินยากขวาง
    ///
    /// ถ้า implementation วาดเป็นวงกลม/วงรัศมี ทั้งสองช่องจะเข้าหรือไม่เข้าพร้อมกันเสมอ → ตกเทสต์นี้
    /// </summary>
    [Fact]
    public void Reachable_DifficultTerrainInOneDirection_IsNotACircle()
    {
        var reachable = MovementCostResolver.Reachable(
            P(0, 0), MovementMode.Walk, DifficultColumnAtX1(), new MovementProfile(BudgetFeet: 10));

        Assert.Equal(10, reachable[P(0, 2)].CostFeet);
        Assert.False(reachable.ContainsKey(P(2, 0)));
    }

    /// <summary>
    /// **หัวใจของ §3.1 ข้อสอง** — "อ้อมไกลกว่ากลับถูกกว่าลุยตรง"
    ///
    /// พื้นเดินยากเป็นบล็อกกว้าง 3 ช่อง (x=1..3, y=-1..1) เป้าหมาย (4,0):
    /// - ลุยตรง 4 ก้าว = 10+10+10+5 = **35 ฟุต**
    /// - อ้อมใต้บล็อก 6 ก้าวบนพื้นสะอาด = 6×5 = **30 ฟุต**
    ///
    /// ก้าวเยอะกว่าแต่ถูกกว่า และด้วยงบ 30 ฟุต (speed มาตรฐาน) ไปถึงได้เฉพาะทางอ้อมเท่านั้น —
    /// implementation ที่คิดจากเส้นตรงอย่างเดียวจะรายงานว่า "ไปไม่ถึง" ซึ่งผิด
    /// </summary>
    [Fact]
    public void Reachable_DetourCheaperThanStraightLine_UsesTheCheaperRoute()
    {
        var block = MapDifficult(
            P(1, -1), P(1, 0), P(1, 1),
            P(2, -1), P(2, 0), P(2, 1),
            P(3, -1), P(3, 0), P(3, 1));

        var straightLine = MovementCostResolver.CostOfPath(
            [P(0, 0), P(1, 0), P(2, 0), P(3, 0), P(4, 0)],
            MovementMode.Walk, block, new MovementProfile(BudgetFeet: 30));

        var reachable = MovementCostResolver.Reachable(
            P(0, 0), MovementMode.Walk, block, new MovementProfile(BudgetFeet: 30));

        Assert.Equal(35, straightLine.TotalFeet);
        Assert.False(straightLine.IsAffordable);

        Assert.True(reachable.ContainsKey(P(4, 0)));
        Assert.Equal(30, reachable[P(4, 0)].CostFeet);
    }

    /// <summary>
    /// novice check ระดับ pathfinding — ด้วยงบเท่ากันเป๊ะ (10 ฟุต) เดินไปไม่ถึง (2,0) เพราะติดพื้น
    /// เดินยาก แต่ "กระโดดข้าม" ไปถึง · นี่คือสิ่งที่ผู้เล่นจะเห็นเป็นพื้นที่ไฮไลต์กว้างขึ้นเมื่อเลือก
    /// วิธีเคลื่อนที่อื่น โดยไม่ต้องอ่านกฎเองเลย
    /// </summary>
    [Fact]
    public void Reachable_JumpMode_ReachesPastDifficultTerrainThatWalkingCannot()
    {
        var terrain = DifficultColumnAtX1();
        var profile = new MovementProfile(BudgetFeet: 10);

        var walking = MovementCostResolver.Reachable(P(0, 0), MovementMode.Walk, terrain, profile);
        var jumping = MovementCostResolver.Reachable(P(0, 0), MovementMode.Jump, terrain, profile);

        Assert.False(walking.ContainsKey(P(2, 0)));
        Assert.True(jumping.ContainsKey(P(2, 0)));
        Assert.Equal(10, jumping[P(2, 0)].CostFeet);
    }

    /// <summary>ปีนแพงเป็นสองเท่าของเดิน (SRD "Climbing") → งบเท่ากันไปได้ครึ่งเดียว</summary>
    [Fact]
    public void Reachable_ClimbWithoutClimbSpeed_ReachesHalfAsFarAsWalking()
    {
        var profile = new MovementProfile(BudgetFeet: 30);

        var climbing = MovementCostResolver.Reachable(P(0, 0), MovementMode.Climb, TerrainField.Empty, profile);

        Assert.Equal(30, climbing[P(3, 0)].CostFeet);
        Assert.False(climbing.ContainsKey(P(4, 0)));
    }

    [Fact]
    public void Reachable_BlockedSquares_AreNeverReachable()
    {
        var wall = new TerrainField(
            FromMap: TerrainLayer.FromSquares(blocked: [P(1, -1), P(1, 0), P(1, 1)]),
            FromEffects: TerrainLayer.Empty);

        var reachable = MovementCostResolver.Reachable(
            P(0, 0), MovementMode.Walk, wall, new MovementProfile(BudgetFeet: 10));

        Assert.False(reachable.ContainsKey(P(1, 0)));
        Assert.False(reachable.ContainsKey(P(2, 0)));
        Assert.True(reachable.ContainsKey(P(0, 1)));
    }

    [Fact]
    public void Reachable_ZeroBudget_OnlyTheOriginSquare()
    {
        var reachable = MovementCostResolver.Reachable(
            P(4, 7), MovementMode.Walk, TerrainField.Empty, new MovementProfile(BudgetFeet: 0));

        Assert.Single(reachable);
        Assert.Equal(0, reachable[P(4, 7)].CostFeet);
    }

    /// <summary>ถูกจับล็อก (§3.3 effect ที่ติดตัวละคร) — ระยะเหลือเต็มแต่ขยับไม่ได้เลย</summary>
    [Fact]
    public void Reachable_Immobilized_OnlyTheOriginSquare()
    {
        var reachable = MovementCostResolver.Reachable(
            P(0, 0), MovementMode.Walk, TerrainField.Empty,
            new MovementProfile(BudgetFeet: 30, IsImmobilized: true));

        Assert.Single(reachable);
    }

    /// <summary>
    /// เทสต์ยึดสองฟังก์ชันให้ตรงกัน: เส้นทางที่ Reachable คืนมา เมื่อเอาไปคิดราคาด้วย CostOfPath
    /// ต้องได้ราคาเท่ากันเป๊ะทุกช่อง (ถ้าหลุดจากกัน ผู้เล่นจะเห็นไฮไลต์ว่าไปได้ แต่พอกดจริงกลับ
    /// ระยะไม่พอ ซึ่งเป็นบั๊กที่โมโหที่สุดสำหรับมือใหม่)
    ///
    /// CostOfPath โยนเมื่อเจอก้าวที่ไม่ติดกัน/ทะลุกำแพง จึงตรวจความต่อเนื่องของเส้นทางไปในตัว
    /// </summary>
    [Fact]
    public void Reachable_EveryReturnedPath_CostsExactlyWhatWasReported()
    {
        var terrain = new TerrainField(
            FromMap: TerrainLayer.FromSquares(difficult: [P(1, 0), P(1, 1)], blocked: [P(-1, -1)]),
            FromEffects: TerrainLayer.FromSquares(difficult: [P(2, 2)]));
        var profile = new MovementProfile(BudgetFeet: 20);

        var reachable = MovementCostResolver.Reachable(P(0, 0), MovementMode.Walk, terrain, profile);

        Assert.NotEmpty(reachable);
        foreach (var (square, entry) in reachable)
        {
            Assert.Equal(square, entry.Square);
            Assert.Equal(P(0, 0), entry.Path[0]);
            Assert.Equal(square, entry.Path[^1]);

            var recomputed = MovementCostResolver.CostOfPath(entry.Path, MovementMode.Walk, terrain, profile);
            Assert.Equal(entry.CostFeet, recomputed.TotalFeet);
            Assert.True(recomputed.IsAffordable);
        }
    }
}
