namespace DnDEz.Rules;

/// <summary>
/// พิกัดช่องบนกริด — เป็น <c>readonly record struct</c> เพื่อให้เทียบเท่ากันด้วยค่า (value equality)
/// และใช้เป็นคีย์ของ <see cref="HashSet{T}"/>/<see cref="Dictionary{TKey,TValue}"/> ได้ตรง ๆ
/// โดยไม่ต้องเขียน GetHashCode เอง (ทั้ง terrain layer และผลลัพธ์ของ Dijkstra พึ่งพาข้อนี้)
///
/// หน่วยเป็น "ช่อง" ไม่ใช่ฟุต — การแปลงเป็นฟุตอยู่ที่ <see cref="MovementCostResolver.FeetPerSquare"/>
/// ที่เดียว และไม่มีขอบเขตแผนที่ในชั้นนี้ (พิกัดติดลบใช้ได้ปกติ) เพราะ engine ไม่รู้จัก scene/แผนที่
/// </summary>
public readonly record struct GridPoint(int X, int Y);

/// <summary>
/// วิธีเคลื่อนที่ที่ใช้ในหนึ่งก้าว — ตัวแปรนี้เป็น "แกนหลัก" ที่ตัดสินราคาต่อช่อง ไม่ใช่ flag ว่าตัวละคร
/// แตะพื้นอยู่หรือไม่
///
/// **เหตุผลที่ต้องเขียนกำกับไว้ กันคนรุ่นหลังแก้กลับด้าน**: action catalog seed ค่า
/// <c>touches_ground = false</c> ให้ท่า <c>climb</c> ซึ่งถ้าเผลอเขียนกฎแบบ "ไม่แตะพื้น ⇒ ไม่คิดผลพื้น"
/// จะได้ผลว่าปีนในพื้นเดินยากฟรี — SRD 5.2 "Climbing" บอกตรงกันข้าม คือปีนใน Difficult Terrain
/// **แพงขึ้น** (*"each foot of movement costs 1 extra foot (2 extra feet in Difficult Terrain)"*)
/// engine จึงคิดราคาจาก <see cref="MovementMode"/> ไม่ใช่จาก boolean เดียวว่าแตะพื้นหรือไม่
/// มีแค่ <see cref="Jump"/> กับ <see cref="Fly"/> เท่านั้นที่ไม่คิดผลพื้นจริง
/// </summary>
public enum MovementMode
{
    /// <summary>เดินบนพื้น — โหมดเดียวที่โดนราคาพื้นเดินยากแบบเต็ม ๆ ตามกฎกลาง</summary>
    Walk,

    /// <summary>กระโดด — SRD 5.2 "Long Jump": *"each foot you jump costs a foot of movement"*
    /// คิดฟุตต่อฟุตตรง ๆ ไม่มีค่าพื้น เพราะระหว่างกระโดดตัวลอยเหนือช่องนั้น</summary>
    Jump,

    /// <summary>ปีน — แพงกว่าเดิน และแพงขึ้นอีกในพื้นเดินยาก ยกเว้นมี Climb Speed</summary>
    Climb,

    /// <summary>บิน — ไม่แตะพื้นจึงไม่คิดค่าพื้นเดินยาก (แต่ยังทะลุช่องที่ถูกปิดกั้นไม่ได้ในรอบนี้)</summary>
    Fly
}

/// <summary>
/// ชั้นข้อมูลพื้นหนึ่งชั้น: เซ็ตของช่องที่เป็นพื้นเดินยาก และเซ็ตของช่องที่เดินผ่านไม่ได้
///
/// เก็บเป็น <see cref="IReadOnlySet{T}"/> เพราะการคิดราคาต้องถาม "ช่องนี้เข้าข่ายไหม" เป็นล้านครั้ง
/// ตอนทำ Dijkstra — ต้องเป็น O(1) ไม่ใช่ไล่ list
/// </summary>
/// <param name="Difficult">ช่องที่เป็นพื้นเดินยากในชั้นนี้</param>
/// <param name="Blocked">ช่องที่เข้าไม่ได้เลยในชั้นนี้ (กำแพง/สิ่งกีดขวางที่กินเต็มช่อง)</param>
public sealed record TerrainLayer(IReadOnlySet<GridPoint> Difficult, IReadOnlySet<GridPoint> Blocked)
{
    /// <summary>ชั้นว่าง — ใช้เป็นค่าเริ่มต้นและใช้ "ลบชั้น effect ทิ้งทั้งชั้น" ตอนเวทหมดเวลา</summary>
    public static TerrainLayer Empty { get; } =
        new(new HashSet<GridPoint>(), new HashSet<GridPoint>());

    /// <summary>
    /// สร้างชั้นจากรายการช่อง — คัดลอกเข้า <see cref="HashSet{T}"/> ของตัวเองเสมอ เพื่อไม่ให้ผู้เรียก
    /// แก้ collection เดิมทีหลังแล้วทำให้ terrain ที่ engine ถืออยู่เปลี่ยนกลางคัน
    /// </summary>
    public static TerrainLayer FromSquares(
        IEnumerable<GridPoint>? difficult = null,
        IEnumerable<GridPoint>? blocked = null) =>
        new(difficult is null ? Empty.Difficult : new HashSet<GridPoint>(difficult),
            blocked is null ? Empty.Blocked : new HashSet<GridPoint>(blocked));
}

/// <summary>
/// พื้นทั้งหมดของฉาก = ชั้นจากแผนที่ + ชั้นจาก effect (spatial-play-design §3.3)
///
/// **เหตุผลที่ต้องแยกสองชั้น ไม่ใช่ยุบเป็นเซ็ตเดียว**: ชั้นแผนที่ (<paramref name="FromMap"/>) คือ
/// สิ่งที่ DM วาดไว้ล่วงหน้าใน scenes.grid ส่วนชั้น effect (<paramref name="FromEffects"/>) คือของที่
/// ผูกกับ encounter อย่าง Web/Grease ซึ่งต้อง **ลบทิ้งได้ทั้งชั้น** ตอนเวทหมดเวลา โดยแผนที่ไม่สกปรกถาวร
/// (<c>terrain with { FromEffects = TerrainLayer.Empty }</c>)
///
/// สองชั้นรวมกันด้วย **OR เท่านั้น ห้ามบวกหรือคูณ** — SRD 5.2 "Difficult Terrain": *"Difficult Terrain
/// isn't cumulative; either a space is Difficult Terrain or it isn't."*
/// </summary>
public sealed record TerrainField(TerrainLayer FromMap, TerrainLayer FromEffects)
{
    /// <summary>พื้นโล่งทั้งฉาก ไม่มีทั้งพื้นเดินยากและสิ่งกีดขวาง</summary>
    public static TerrainField Empty { get; } = new(TerrainLayer.Empty, TerrainLayer.Empty);

    /// <summary>ช่องนี้เป็นพื้นเดินยากไหม — OR ของสองชั้น ช่องที่ยากจากทั้งสองแหล่งก็ยังนับเป็น
    /// "ยาก" หนึ่งครั้ง ไม่ทบกัน</summary>
    public bool IsDifficult(GridPoint square) =>
        FromMap.Difficult.Contains(square) || FromEffects.Difficult.Contains(square);

    /// <summary>ช่องนี้เข้าไม่ได้ไหม — OR ของสองชั้นเช่นกัน</summary>
    public bool IsBlocked(GridPoint square) =>
        FromMap.Blocked.Contains(square) || FromEffects.Blocked.Contains(square);
}

/// <summary>
/// effect ที่ "ติดตัวละคร" ตอนคิดการเคลื่อนที่ (§3.3 แกนที่สอง — คนละแกนกับพื้นที่ติดกับช่อง
/// แต่เข้าสมการเดียวกันตอนคำนวณ)
///
/// เป็น record ล้วน ไม่รู้จัก Character/ผู้ใช้/ฐานข้อมูล — ชั้นบนเป็นคนแปลงสถานะจริง (speed ปัจจุบัน,
/// Climb Speed จาก race/spell, สภาพ Grappled/Restrained/Paralyzed) มาเป็นสามค่านี้
/// </summary>
/// <param name="BudgetFeet">ระยะที่ใช้ได้จริงในตานี้ (ฟุต) ห้ามติดลบ</param>
/// <param name="HasClimbSpeed">มี Climb Speed และใช้มันปีนอยู่ — ยกเว้นค่าปีนส่วนเกิน</param>
/// <param name="IsImmobilized">ขยับไม่ได้เลย (ถูกจับล็อก/speed = 0) แม้ระยะจะเหลือ</param>
public sealed record MovementProfile(int BudgetFeet, bool HasClimbSpeed = false, bool IsImmobilized = false)
{
    private readonly int _budgetFeet = BudgetFeet >= 0
        ? BudgetFeet
        : throw new ArgumentOutOfRangeException(nameof(BudgetFeet), BudgetFeet, "BudgetFeet ห้ามติดลบ");

    /// <summary>ระยะที่ใช้ได้จริงในตานี้ (ฟุต) — ติดลบถือเป็น caller bug โยนทันทีตั้งแต่ตอนสร้าง
    /// ตามแพทเทิร์นเดียวกับ <see cref="ActionEconomyResolver.StartTurn"/></summary>
    public int BudgetFeet
    {
        get => _budgetFeet;
        init => _budgetFeet = value >= 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(BudgetFeet), value, "BudgetFeet ห้ามติดลบ");
    }
}

/// <summary>
/// ผลการคิดราคาของ "เส้นทางที่กำหนดมาแล้ว"
/// </summary>
/// <param name="TotalFeet">ราคาจริงรวมทั้งเส้นทาง — รายงานตามจริงเสมอแม้จ่ายไม่ไหว ชั้นบนจะได้บอก
/// ผู้เล่นได้ว่า "ขาดอีกกี่ฟุต" ไม่ใช่แค่ปฏิเสธเฉย ๆ</param>
/// <param name="IsAffordable">จ่ายไหวไหมด้วยงบและสภาพปัจจุบัน</param>
/// <param name="RequiresLandingCheck">ต้องทอย DC 10 Dexterity (Acrobatics) ตอนลงพื้นไหม
/// — ไม่เกี่ยวกับราคา เป็นผลที่ตามมาอย่างเดียว</param>
public sealed record PathCost(int TotalFeet, bool IsAffordable, bool RequiresLandingCheck);

/// <summary>
/// หนึ่งช่องในผลลัพธ์ "เดินได้ถึงไหน" พร้อมเส้นทางที่ถูกที่สุดที่ทำให้ได้ราคานั้นจริง
/// </summary>
/// <param name="Square">ช่องปลายทาง</param>
/// <param name="CostFeet">ราคาที่ถูกที่สุดจากจุดเริ่มมาถึงช่องนี้</param>
/// <param name="Path">เส้นทางที่ให้ราคานั้น เริ่มที่ช่องต้นทางและจบที่ <paramref name="Square"/>
/// — ต้องเป็นเส้นทางที่ <see cref="MovementCostResolver.CostOfPath"/> คิดแล้วได้
/// <paramref name="CostFeet"/> เป๊ะ ไม่งั้นผู้เล่นจะเห็นไฮไลต์ว่าไปได้ แต่พอกดจริงกลับระยะไม่พอ</param>
public sealed record ReachableSquare(GridPoint Square, int CostFeet, IReadOnlyList<GridPoint> Path);
