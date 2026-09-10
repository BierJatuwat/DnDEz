namespace DnDEz.Rules.Tests;

/// <summary>
/// ต้นทุนการเคลื่อนที่ของ "เส้นทางที่กำหนดมาแล้ว" (spatial-play-design §3.1–§3.3)
///
/// ทุกตัวเลขในไฟล์นี้อ้าง SRD 5.2 ฉบับ CC (SRD_CC_v5.2.pdf) ที่เปิดอ่านจริงตอนเขียนเทสต์ ไม่ใช่
/// ความจำ — ข้อความที่ยกมาอยู่ใน comment ของแต่ละเทสต์ เพราะ §3.2 ของ design doc เขียนกำกับไว้เองว่า
/// "รายละเอียดกฎจริงยังไม่ได้ตรวจกับ SRD 5.2 ต้องเช็คตอนลงรายละเอียด"
///
/// สองข้อที่ตรวจแล้วพบว่า design doc เดาไว้ผิด และเทสต์ในไฟล์นี้ยึดตาม SRD ไม่ใช่ตาม doc:
/// 1. **ปีนไม่ได้ "ไม่คิดผลพื้น"** — SRD: ปีนเสีย 1 ฟุตเพิ่มต่อฟุต และ *2 ฟุตเพิ่มในพื้นเดินยาก*
///    (แพงกว่าเดินด้วยซ้ำ ไม่ใช่ยกเว้น) มีแค่กระโดด/บินที่ไม่คิดผลพื้นจริง
/// 2. **พื้นเดินยากสองแหล่งไม่คูณกัน** — SRD: "Difficult Terrain isn't cumulative; either a space
///    is Difficult Terrain or it isn't" → รวมสองแหล่งเป็น OR ไม่ใช่คูณ (ดู §3.3)
/// </summary>
public class MovementCostResolverTests
{
    private static GridPoint P(int x, int y) => new(x, y);

    private static TerrainField MapDifficult(params GridPoint[] squares) =>
        new(TerrainLayer.FromSquares(difficult: squares), TerrainLayer.Empty);

    private static MovementProfile Budget(int feet) => new(BudgetFeet: feet);

    // ---------- ตารางต้นทุนพื้นฐาน ----------

    /// <summary>
    /// SRD 5.2 "Playing the Game" (กริด): หนึ่งช่อง = 5 ฟุต — "30 feet translates into 6 squares"
    /// </summary>
    [Fact]
    public void CostOfPath_WalkOnClearGround_ChargesFiveFeetPerSquareEntered()
    {
        var path = new[] { P(0, 0), P(1, 0), P(2, 0), P(3, 0) };

        var cost = MovementCostResolver.CostOfPath(path, MovementMode.Walk, TerrainField.Empty, Budget(30));

        Assert.Equal(15, cost.TotalFeet);
    }

    /// <summary>
    /// SRD 5.2 "Difficult Terrain": "every foot of movement in that space costs 1 extra foot.
    /// For example, moving 5 feet through Difficult Terrain costs 10 feet of movement."
    /// </summary>
    [Fact]
    public void CostOfPath_WalkThroughDifficultSquare_ChargesTenFeetForThatSquareOnly()
    {
        var terrain = MapDifficult(P(1, 0));
        var path = new[] { P(0, 0), P(1, 0), P(2, 0) };

        var cost = MovementCostResolver.CostOfPath(path, MovementMode.Walk, terrain, Budget(30));

        // 10 (ช่องเดินยาก) + 5 (ช่องปกติ) — ไม่ใช่ 15×2 ทั้งเส้นทาง
        Assert.Equal(15, cost.TotalFeet);
    }

    /// <summary>
    /// SRD 5.2: "It costs 1 square of movement to enter an unoccupied square that's adjacent to
    /// your space (orthogonally **or diagonally** adjacent)" — ทแยงราคาเท่าตรง (กฎสลับ 5/10 ฟุต
    /// เป็น optional variant ที่ไม่ได้อยู่ใน SRD ห้ามเผลอใส่)
    /// </summary>
    [Fact]
    public void CostOfPath_DiagonalStep_CostsSameAsOrthogonalStep()
    {
        var diagonal = new[] { P(0, 0), P(1, 1), P(2, 2) };
        var orthogonal = new[] { P(0, 0), P(1, 0), P(2, 0) };

        var diagonalCost = MovementCostResolver.CostOfPath(diagonal, MovementMode.Walk, TerrainField.Empty, Budget(30));
        var orthogonalCost = MovementCostResolver.CostOfPath(orthogonal, MovementMode.Walk, TerrainField.Empty, Budget(30));

        Assert.Equal(10, diagonalCost.TotalFeet);
        Assert.Equal(orthogonalCost.TotalFeet, diagonalCost.TotalFeet);
    }

    /// <summary>ราคาคิดตอน "เข้า" ช่อง ช่องที่ยืนอยู่ตอนเริ่มจึงไม่คิดเงินแม้เป็นพื้นเดินยาก
    /// (ไม่งั้นคนที่ติดอยู่ในหนองน้ำจะโดนชาร์จฟรีทุกเทิร์นที่ขยับ)</summary>
    [Fact]
    public void CostOfPath_OriginSquareIsDifficult_NotCharged()
    {
        var terrain = MapDifficult(P(0, 0));
        var path = new[] { P(0, 0), P(1, 0) };

        var cost = MovementCostResolver.CostOfPath(path, MovementMode.Walk, terrain, Budget(30));

        Assert.Equal(5, cost.TotalFeet);
    }

    [Fact]
    public void CostOfPath_SingleSquarePath_CostsNothing()
    {
        var cost = MovementCostResolver.CostOfPath([P(2, 2)], MovementMode.Walk, TerrainField.Empty, Budget(30));

        Assert.Equal(0, cost.TotalFeet);
        Assert.True(cost.IsAffordable);
    }

    // ---------- §3.3 พื้นสองแหล่ง ----------

    /// <summary>
    /// **ข้อที่ design doc เดาไว้ผิด** — SRD 5.2 "Difficult Terrain": *"Difficult Terrain isn't
    /// cumulative; either a space is Difficult Terrain or it isn't."* และย้ำอีกครั้งในหมวด Combat:
    /// *"Every foot of movement in Difficult Terrain costs 1 extra foot, even if multiple things
    /// in a space count as Difficult Terrain."*
    ///
    /// ช่องที่เป็นพื้นเดินยากทั้งจากแผนที่ (หนองน้ำ) และจาก effect (Web) พร้อมกัน ยังคง 10 ฟุต
    /// ไม่ใช่ 20 — ถ้าปล่อยให้คูณกันจะกลายเป็นบั๊กที่ผู้เล่นเจอเฉพาะตอนซวยซ้อน แล้วไม่มีใครจับได้
    /// </summary>
    [Fact]
    public void CostOfPath_SquareDifficultFromBothMapAndEffect_StillCostsTenFeet_NotCumulative()
    {
        var terrain = new TerrainField(
            FromMap: TerrainLayer.FromSquares(difficult: [P(1, 0)]),
            FromEffects: TerrainLayer.FromSquares(difficult: [P(1, 0)]));

        var cost = MovementCostResolver.CostOfPath(
            [P(0, 0), P(1, 0)], MovementMode.Walk, terrain, Budget(30));

        Assert.Equal(10, cost.TotalFeet);
    }

    /// <summary>พื้นเดินยากจาก effect อย่างเดียว (Grease ที่ร่ายลงบนพื้นสะอาด) ต้องคิดเท่ากับ
    /// พื้นเดินยากจากแผนที่ — สองชั้นรวมกันแบบ OR</summary>
    [Fact]
    public void CostOfPath_SquareDifficultFromEffectLayerOnly_CostsSameAsMapDifficult()
    {
        var effectOnly = new TerrainField(
            FromMap: TerrainLayer.Empty,
            FromEffects: TerrainLayer.FromSquares(difficult: [P(1, 0)]));

        var cost = MovementCostResolver.CostOfPath(
            [P(0, 0), P(1, 0)], MovementMode.Walk, effectOnly, Budget(30));

        Assert.Equal(10, cost.TotalFeet);
    }

    /// <summary>
    /// เหตุผลที่ §3.3 บังคับให้เก็บสองแหล่งแยกกัน: เวทหมดเวลาแล้วต้องลบชั้น effect ทิ้งได้ทั้งชั้น
    /// โดยแผนที่ไม่สกปรกถาวร — เทสต์นี้พิสูจน์ว่าโครงสร้างข้อมูลทำแบบนั้นได้จริง ไม่ใช่แค่ตั้งใจไว้
    /// </summary>
    [Fact]
    public void CostOfPath_DroppingEffectLayer_RevertsToMapOnlyCost()
    {
        var duringSpell = new TerrainField(
            FromMap: TerrainLayer.Empty,
            FromEffects: TerrainLayer.FromSquares(difficult: [P(1, 0), P(2, 0)]));

        var afterSpellExpires = duringSpell with { FromEffects = TerrainLayer.Empty };

        var during = MovementCostResolver.CostOfPath(
            [P(0, 0), P(1, 0), P(2, 0)], MovementMode.Walk, duringSpell, Budget(30));
        var after = MovementCostResolver.CostOfPath(
            [P(0, 0), P(1, 0), P(2, 0)], MovementMode.Walk, afterSpellExpires, Budget(30));

        Assert.Equal(20, during.TotalFeet);
        Assert.Equal(10, after.TotalFeet);
    }

    // ---------- §3.2 วิธีเคลื่อนที่ ----------

    /// <summary>
    /// **novice check ของ Part นี้** (ตัวอย่างที่เจ้าของโปรเจกต์ยกไว้ตอนออกแบบ): เดินลุยพื้นเดินยาก
    /// เสียระยะเร็วกว่าปกติ แต่ "กระโดดข้าม" ไม่เสียเพิ่ม
    ///
    /// SRD 5.2 "Long Jump": *"each foot you jump costs a foot of movement"* — คิดฟุตต่อฟุตตรง ๆ
    /// ไม่มีตัวคูณของพื้น เพราะระหว่างกระโดดตัวลอยอยู่เหนือช่องนั้น
    /// </summary>
    [Fact]
    public void CostOfPath_JumpOverDifficultTerrain_CostsLessThanWalkingTheSamePath()
    {
        var terrain = MapDifficult(P(1, 0), P(2, 0));
        var path = new[] { P(0, 0), P(1, 0), P(2, 0) };

        var walking = MovementCostResolver.CostOfPath(path, MovementMode.Walk, terrain, Budget(30));
        var jumping = MovementCostResolver.CostOfPath(path, MovementMode.Jump, terrain, Budget(30));

        Assert.Equal(20, walking.TotalFeet);
        Assert.Equal(10, jumping.TotalFeet);
    }

    /// <summary>บินข้ามพื้นเดินยากก็ไม่คิดผลพื้นเช่นกัน — พื้นเดินยากเป็นเรื่องของ "พื้น" ที่คนบิน
    /// ไม่ได้แตะ (SRD "Fly Speed": ใช้เดินทางผ่านอากาศ ไม่มีข้อความให้บวกราคาพื้นเลย)</summary>
    [Fact]
    public void CostOfPath_FlyOverDifficultTerrain_NoExtraCost()
    {
        var terrain = MapDifficult(P(1, 0), P(2, 0));

        var cost = MovementCostResolver.CostOfPath(
            [P(0, 0), P(1, 0), P(2, 0)], MovementMode.Fly, terrain, Budget(30));

        Assert.Equal(10, cost.TotalFeet);
    }

    /// <summary>
    /// **ข้อที่ design doc เดาไว้ผิด** — §3.2 เขียนว่า "ปีนข้าม → ไม่คิด [ผลพื้น]" แต่ SRD 5.2
    /// "Climbing" บอกตรงข้าม: *"While you're climbing, each foot of movement costs 1 extra foot
    /// (2 extra feet in Difficult Terrain). You ignore this extra cost if you have a Climb Speed."*
    ///
    /// ปีนบนพื้นปกติ = 1+1 = 2 ฟุตต่อฟุต → 10 ฟุตต่อช่อง
    /// </summary>
    [Fact]
    public void CostOfPath_ClimbWithoutClimbSpeed_CostsOneExtraFootPerFoot()
    {
        var cost = MovementCostResolver.CostOfPath(
            [P(0, 0), P(1, 0)], MovementMode.Climb, TerrainField.Empty, Budget(30));

        Assert.Equal(10, cost.TotalFeet);
    }

    /// <summary>
    /// ปีนในพื้นเดินยาก = 1 (ฐาน) + 2 (extra ตามวงเล็บของกฎ Climbing) = 3 ฟุตต่อฟุต → 15 ฟุตต่อช่อง
    ///
    /// **ห้ามได้ 20** — ตัวเลข "2 extra feet in Difficult Terrain" ในกฎ Climbing คือยอดรวมของ
    /// extra ทั้งหมดในพื้นเดินยากอยู่แล้ว ไม่ใช่ให้เอา ×2 ของปีนไปคูณกับ ×2 ของพื้นเดินยากอีกที
    /// (เช็คยันได้ว่ากฎตั้งใจแบบนี้: ถ้าจะคูณกันจริงวงเล็บต้องเขียน "3 extra feet")
    /// </summary>
    [Fact]
    public void CostOfPath_ClimbThroughDifficultTerrain_CostsTwoExtraFeetPerFoot_NotMultiplied()
    {
        var terrain = MapDifficult(P(1, 0));

        var cost = MovementCostResolver.CostOfPath(
            [P(0, 0), P(1, 0)], MovementMode.Climb, terrain, Budget(30));

        Assert.Equal(15, cost.TotalFeet);
    }

    /// <summary>
    /// SRD: *"You ignore this extra cost if you have a Climb Speed and use it to climb."*
    /// → ค่าปีนส่วนเกินหายไป เหลือราคาเท่าเดินปกติ
    ///
    /// **การตีความที่บันทึกไว้ให้ตรวจซ้ำได้**: ตัวบทกำกวมว่า "this extra cost" กินรวมส่วนของพื้น
    /// เดินยากด้วยหรือไม่ — ตีความว่า Climb Speed ยกเว้นเฉพาะ *ค่าปีน* ส่วนกฎพื้นเดินยากยังใช้ตามปกติ
    /// เพราะกฎพื้นเดินยากเป็นกฎกลางที่กินทุกฟุตของการเคลื่อนที่ ถ้าตีความอีกทางจะได้ผลประหลาดว่า
    /// "ปีนในหนองน้ำถูกกว่าเดินในหนองน้ำ"
    /// </summary>
    [Fact]
    public void CostOfPath_ClimbWithClimbSpeed_IgnoresClimbSurchargeButStillPaysDifficultTerrain()
    {
        var profile = new MovementProfile(BudgetFeet: 30, HasClimbSpeed: true);
        var terrain = MapDifficult(P(2, 0));

        var onClearGround = MovementCostResolver.CostOfPath(
            [P(0, 0), P(1, 0)], MovementMode.Climb, terrain, profile);
        var throughDifficult = MovementCostResolver.CostOfPath(
            [P(1, 0), P(2, 0)], MovementMode.Climb, terrain, profile);

        Assert.Equal(5, onClearGround.TotalFeet);
        Assert.Equal(10, throughDifficult.TotalFeet);
    }

    // ---------- กฎลงพื้นหลังกระโดด (ข้อที่ dispatch สั่งให้ไปเปิด SRD ยืนยันก่อน) ----------

    /// <summary>
    /// คำตอบของคำถามที่ §3.2 แขวนไว้ว่า "กระโดดข้ามพื้นเดินยากคิดยังไงตอนลงพื้น" —
    /// SRD 5.2 "Long Jump": *"If you land in Difficult Terrain, you must succeed on a DC 10
    /// Dexterity (Acrobatics) check or have the Prone condition."*
    ///
    /// สรุป: **ไม่ใช่เรื่องราคา แต่เป็นผลที่ตามมา** engine จึงคิดราคาเท่าเดิม (ฟุตต่อฟุต) แล้วยก
    /// ธงบอกว่าต้องทอย ส่วนการทอยจริงและการติดสถานะ Prone เป็นงานของชั้นบน (engine ไม่ทอยเอง
    /// เพราะการคิดราคาไม่ต้องใช้เต๋าเลย — ไม่ต้องฉีด IDiceRoller เข้ามาให้เปลืองเปล่า)
    /// </summary>
    [Fact]
    public void CostOfPath_JumpLandingInDifficultTerrain_RequiresLandingCheck()
    {
        var terrain = MapDifficult(P(2, 0));

        var cost = MovementCostResolver.CostOfPath(
            [P(0, 0), P(1, 0), P(2, 0)], MovementMode.Jump, terrain, Budget(30));

        Assert.True(cost.RequiresLandingCheck);
        Assert.Equal(10, MovementCostResolver.DifficultLandingDc);
        // ราคายังไม่เปลี่ยน — การลงพื้นในพื้นเดินยากไม่ได้แพงขึ้น แค่เสี่ยงล้ม
        Assert.Equal(10, cost.TotalFeet);
    }

    /// <summary>กระโดดข้ามพื้นเดินยากแต่ไปลงบนพื้นปกติ = ไม่ต้องทอยอะไรเลย (นี่คือเหตุผลที่
    /// "กระโดดข้าม" เป็นทางเลือกที่ดีจริงในตัวอย่าง novice check ไม่ใช่แค่ย้ายความเสี่ยงไปที่อื่น)</summary>
    [Fact]
    public void CostOfPath_JumpOverDifficultTerrainButLandingOnClearGround_NoLandingCheck()
    {
        var terrain = MapDifficult(P(1, 0));

        var cost = MovementCostResolver.CostOfPath(
            [P(0, 0), P(1, 0), P(2, 0)], MovementMode.Jump, terrain, Budget(30));

        Assert.False(cost.RequiresLandingCheck);
    }

    /// <summary>เดินเข้าไปหยุดในพื้นเดินยากไม่ต้องทอยอะไร — กฎ DC 10 นี้ผูกกับ "การกระโดด"
    /// เท่านั้น (อยู่ในหัวข้อ Long Jump) ไม่ใช่กฎทั่วไปของพื้นเดินยาก</summary>
    [Fact]
    public void CostOfPath_WalkEndingInDifficultTerrain_NoLandingCheck()
    {
        var terrain = MapDifficult(P(1, 0));

        var cost = MovementCostResolver.CostOfPath(
            [P(0, 0), P(1, 0)], MovementMode.Walk, terrain, Budget(30));

        Assert.False(cost.RequiresLandingCheck);
    }

    // ---------- งบเคลื่อนที่ + effect ที่ติดตัว (§3.3 แกนที่สอง) ----------

    [Fact]
    public void CostOfPath_CostWithinBudget_IsAffordable()
    {
        var cost = MovementCostResolver.CostOfPath(
            [P(0, 0), P(1, 0), P(2, 0)], MovementMode.Walk, TerrainField.Empty, Budget(10));

        Assert.Equal(10, cost.TotalFeet);
        Assert.True(cost.IsAffordable);
    }

    [Fact]
    public void CostOfPath_CostExceedsBudget_NotAffordableButStillReportsCost()
    {
        var terrain = MapDifficult(P(1, 0), P(2, 0));

        var cost = MovementCostResolver.CostOfPath(
            [P(0, 0), P(1, 0), P(2, 0)], MovementMode.Walk, terrain, Budget(15));

        // ยังบอกราคาจริงเสมอ ไม่ปัดเป็น 0 หรือ throw — ชั้นบนต้องเอาไปบอกผู้เล่นว่า "ขาดอีกเท่าไหร่"
        Assert.Equal(20, cost.TotalFeet);
        Assert.False(cost.IsAffordable);
    }

    /// <summary>
    /// §3.3: "สกิลที่จำกัดการเคลื่อนที่ (ถูกจับล็อก/ชะลอ/speed=0) เป็น effect ที่ติดตัวละคร
    /// ไม่ใช่ติดพื้น — คนละแกน แต่เข้าสมการเดียวกันตอนคำนวณ" · ถูกจับล็อกแล้วขยับไม่ได้เลย
    /// แม้ระยะที่เหลือจะพอ
    /// </summary>
    [Fact]
    public void CostOfPath_Immobilized_NotAffordableEvenWhenBudgetIsEnough()
    {
        var profile = new MovementProfile(BudgetFeet: 30, IsImmobilized: true);

        var cost = MovementCostResolver.CostOfPath(
            [P(0, 0), P(1, 0)], MovementMode.Walk, TerrainField.Empty, profile);

        Assert.Equal(5, cost.TotalFeet);
        Assert.False(cost.IsAffordable);
    }

    /// <summary>ถูกจับล็อกแต่ "ไม่ขยับ" ยังถือว่าทำได้ — การอยู่เฉยไม่ใช่การเคลื่อนที่</summary>
    [Fact]
    public void CostOfPath_ImmobilizedButNotMoving_IsStillAffordable()
    {
        var profile = new MovementProfile(BudgetFeet: 0, IsImmobilized: true);

        var cost = MovementCostResolver.CostOfPath([P(0, 0)], MovementMode.Walk, TerrainField.Empty, profile);

        Assert.True(cost.IsAffordable);
    }

    // ---------- input ที่ผิดสัญญา = caller bug ต้อง throw (ตามแพทเทิร์น ActionEconomyResolver) ----------

    [Fact]
    public void CostOfPath_StepToNonAdjacentSquare_Throws()
    {
        Assert.Throws<ArgumentException>(() => MovementCostResolver.CostOfPath(
            [P(0, 0), P(3, 0)], MovementMode.Walk, TerrainField.Empty, Budget(30)));
    }

    [Fact]
    public void CostOfPath_EmptyPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => MovementCostResolver.CostOfPath(
            [], MovementMode.Walk, TerrainField.Empty, Budget(30)));
    }

    [Fact]
    public void CostOfPath_PathEntersBlockedSquare_Throws()
    {
        var terrain = new TerrainField(
            FromMap: TerrainLayer.FromSquares(blocked: [P(1, 0)]),
            FromEffects: TerrainLayer.Empty);

        Assert.Throws<ArgumentException>(() => MovementCostResolver.CostOfPath(
            [P(0, 0), P(1, 0)], MovementMode.Walk, terrain, Budget(30)));
    }

    /// <summary>
    /// SRD 5.2 "Corners": *"Diagonal movement can't cross the corner of a wall, a large tree, or
    /// another terrain feature that fills its space."* — เดินทแยงเฉียดมุมกำแพงไม่ได้
    /// </summary>
    [Fact]
    public void CostOfPath_DiagonalCuttingWallCorner_Throws()
    {
        var terrain = new TerrainField(
            FromMap: TerrainLayer.FromSquares(blocked: [P(1, 0)]),
            FromEffects: TerrainLayer.Empty);

        Assert.Throws<ArgumentException>(() => MovementCostResolver.CostOfPath(
            [P(0, 0), P(1, 1)], MovementMode.Walk, terrain, Budget(30)));
    }

    /// <summary>กำแพงกันเฉพาะการเดินบนพื้น — บินข้ามกำแพงเตี้ยไม่ได้ถูกกันโดย layer นี้
    /// (สิ่งกีดขวางที่กันการบินต้องเป็นคนละชั้นข้อมูล ซึ่งยังไม่มีในรอบนี้) จึงล็อกพฤติกรรมไว้ว่า
    /// blocked = กันทุกโหมดไปก่อน จะได้ไม่มีใครเข้าใจผิดว่าบินทะลุได้แล้ว</summary>
    [Fact]
    public void CostOfPath_FlyIntoBlockedSquare_AlsoThrows()
    {
        var terrain = new TerrainField(
            FromMap: TerrainLayer.FromSquares(blocked: [P(1, 0)]),
            FromEffects: TerrainLayer.Empty);

        Assert.Throws<ArgumentException>(() => MovementCostResolver.CostOfPath(
            [P(0, 0), P(1, 0)], MovementMode.Fly, terrain, Budget(30)));
    }

    [Fact]
    public void MovementProfile_NegativeBudget_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MovementProfile(BudgetFeet: -5));
    }
}
