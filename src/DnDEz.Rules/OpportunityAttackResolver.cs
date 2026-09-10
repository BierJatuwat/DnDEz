namespace DnDEz.Rules;

/// <summary>
/// ตัวละคร/มอนสเตอร์หนึ่งตัวที่ "ถือสิทธิ์" opportunity attack อยู่ ณ ขณะที่มีคนเดินผ่าน
///
/// ทุกฟิลด์เป็นสิ่งที่ชั้นบนคำนวณมาให้แล้ว — engine ไม่ไปดึงอะไรเองเลย ตามหลักเดียวกับ
/// <see cref="AffordanceTarget"/> ที่ความปลอดภัยมาจาก "รับอะไรเข้ามา" ไม่ใช่ "กรองอะไรออกไป"
/// </summary>
/// <param name="Id">รหัสผู้ร่วมฉากที่ถือสิทธิ์ — ติดกลับไปกับ trigger ให้ชั้นบนสั่งทอยต่อได้</param>
/// <param name="Name">ชื่อที่ผู้เดินมีสิทธิ์เห็น (ติดไปกับ trigger เพื่อขึ้นคำเตือน ไม่ให้ client ไปค้นเอง)</param>
/// <param name="Position">ช่องที่ยืนอยู่ตอนนี้ — ถือว่าไม่ขยับตลอดการเคลื่อนที่ครั้งนี้</param>
/// <param name="ReachFeet">
/// ระยะเอื้อมเป็นฟุต · SRD 5.2: *"Certain creatures have melee attacks with a reach greater than
/// 5 feet"* จึงห้าม hardcode 5 ไว้ใน engine ไม่งั้น OA ของมอนสเตอร์ตัวใหญ่จะผิดหมด
/// </param>
/// <param name="CanSeeMover">
/// มองเห็นผู้เคลื่อนที่ไหม · SRD 5.2: *"You can make an Opportunity Attack when a creature
/// **that you can see** leaves your reach"* — engine ไม่รู้จักหมอกของ Part 6 เอง ชั้นบนคำนวณมาให้
/// </param>
/// <param name="ReactionAvailable">
/// ยังมี reaction เหลือในรอบนี้ไหม · OA *"takes a Reaction"* และหนึ่งตัวมี reaction เดียวต่อรอบ
/// </param>
public sealed record ReachThreat(
    Guid Id,
    string Name,
    GridPoint Position,
    int ReachFeet,
    bool CanSeeMover,
    bool ReactionAvailable);

/// <summary>
/// จังหวะหนึ่งจังหวะที่การเคลื่อนที่ต้อง "หยุดค้าง" ไว้ให้ผู้ถือสิทธิ์ตัดสินใจใช้ reaction
///
/// การมีอยู่ของ record นี้แปลว่า **ได้สิทธิ์ทอยจริง** — เคสที่ถูกตัดสิทธิ์ (Disengage / มองไม่เห็น /
/// reaction หมด) ไม่เคยถูกสร้างขึ้นมาเลย ไม่ใช่สร้างแล้วติดธงให้ชั้นบนกรองเอง แพทเทิร์นเดียวกับ
/// <see cref="Affordance"/> ที่ของทำไม่ได้จะหายไปตั้งแต่ใน resolver
/// </summary>
/// <param name="ThreatId">รหัสผู้ถือสิทธิ์</param>
/// <param name="ThreatName">ชื่อผู้ถือสิทธิ์ สำหรับขึ้นคำเตือนให้ผู้เดินเห็น**ก่อนกด** (§3.4)</param>
/// <param name="LeavesAtStepIndex">
/// ดัชนีของช่องในเส้นทางที่ "หลุดระยะ" — คือช่องที่ผู้เดินกำลังจะก้าวเข้าไปตอนถูกสวน (ค่าต่ำสุดคือ 1
/// เพราะช่องที่ 0 คือช่องที่ยืนอยู่แล้ว ยังไม่ได้ขยับ)
/// </param>
/// <param name="AttackFromSquare">
/// ช่องที่ผู้เดินยืนอยู่ตอนถูกสวน = **ช่องสุดท้ายที่ยังอยู่ในระยะ** ไม่ใช่ช่องปลายทาง ตามตัวบท
/// *"The attack occurs right before it leaves your reach."* — ชั้นบนต้องหยุดการเคลื่อนที่ที่ช่องนี้
/// เป๊ะ ๆ ก่อนรัน reaction ถ้าหยุดผิดช่อง ระยะตอนคิดผลโจมตีจะผิดตามไปด้วย
/// </param>
public sealed record OpportunityAttackTrigger(
    Guid ThreatId,
    string ThreatName,
    int LeavesAtStepIndex,
    GridPoint AttackFromSquare);

/// <summary>
/// ตรวจจับ opportunity attack จากเส้นทางเดินหนึ่งเส้น (spatial-play-design §3.4) — เลขคณิตล้วน
/// ไม่มี RNG ไม่มี I/O **ไม่รับ <see cref="IDiceRoller"/>** เหมือน <see cref="MovementCostResolver"/>
/// และ <see cref="AffordanceResolver"/> · คลาสนี้ตอบแค่ว่า "ใครได้สิทธิ์สวน และสวนตอนผู้เดินอยู่ช่องไหน"
/// ส่วนการทอยว่าโดนหรือไม่โดนเป็นงานของ <see cref="AttackResolver"/> ที่มีอยู่แล้ว ห้ามทอยอะไรที่นี่
///
/// **ตัวบท SRD 5.2 ฉบับ CC ที่กฎในไฟล์นี้อ้างอิง** (หมวด Combat "Opportunity Attacks"):
/// <list type="bullet">
///   <item>*"You can make an Opportunity Attack when a creature that you can see leaves your reach.
///     To make the attack, take a Reaction to make one melee attack ... The attack occurs right
///     before it leaves your reach."*</item>
///   <item>*"You can avoid provoking an Opportunity Attack by taking the Disengage action."*</item>
/// </list>
///
/// สามจุดที่ตัวบทระบุชัดและเดาผิดกันบ่อย:
/// <list type="number">
///   <item>trigger คือ **"ออกจาก" ระยะ** ไม่ใช่ "เข้ามาใน" ระยะ และไม่ใช่ "เดินอยู่ในระยะ"</item>
///   <item>โจมตีจาก **ช่องสุดท้ายที่ยังอยู่ในระยะ** ไม่ใช่ช่องปลายทาง</item>
///   <item>*"a creature **that you can see**"* — คนที่มองไม่เห็นผู้เดิน ไม่ได้สิทธิ์</item>
/// </list>
///
/// ระยะนับแบบ Chebyshev × <see cref="MovementCostResolver.FeetPerSquare"/> ฟุตต่อช่อง เลนส์เดียวกับ
/// Part 8 (การคิดราคาเดิน) และ Part 9 (affordance) — ทแยงเท่าตรง ห้ามเผลอใส่กฎสลับ 5/10 ฟุตต่อทแยง
/// ซึ่งเป็น optional variant ที่ไม่ได้อยู่ใน SRD
///
/// **ไม่ตรวจความต่อเนื่องของเส้นทางที่นี่โดยตั้งใจ** — <see cref="MovementCostResolver.CostOfPath"/>
/// โยน <see cref="ArgumentException"/> ให้อยู่แล้วเมื่อก้าวไม่ติดกันหรือเดินทแยงเฉียดมุมกำแพง ถ้าลอก
/// กฎนั้นมาไว้ที่นี่ด้วยจะกลายเป็นที่ที่สองที่เขียนผิดได้ แล้วสองที่จะตอบขัดกันเองวันที่มีคนแก้ฝั่งเดียว
/// หน้าที่ของไฟล์นี้คือกฎ OA ล้วน ๆ
/// </summary>
public static class OpportunityAttackResolver
{
    /// <summary>
    /// หาทุกจังหวะที่เส้นทางนี้จะโดนสวน เรียงตามลำดับก้าวที่เกิดจริง
    ///
    /// **หนึ่ง trigger ต่อหนึ่งผู้ถือสิทธิ์เท่านั้น (ครั้งแรกที่หลุดระยะ)** — ถ้าผู้เดินออกจากระยะ กลับ
    /// เข้ามาใหม่ แล้วออกอีก ครั้งหลังไม่นับ เพราะ OA กิน reaction และหนึ่งตัวมี reaction เดียวต่อรอบ
    /// ถ้าคืนครั้งที่สองมาด้วย ชั้นบนจะเด้ง prompt ให้คนที่ตอบอะไรไม่ได้แล้ว
    /// </summary>
    /// <param name="path">
    /// เส้นทางเดินเรียงตามลำดับ ช่องแรกคือช่องที่ยืนอยู่ก่อนขยับ · เส้นทางช่องเดียว = ไม่ขยับ =
    /// ไม่มีทางเกิด OA
    /// </param>
    /// <param name="threats">ผู้ที่อาจได้สิทธิ์สวน ลำดับที่ส่งเข้ามาไม่มีผลต่อลำดับผลลัพธ์</param>
    /// <param name="moverIsDisengaging">
    /// ผู้เดินใช้ท่า Disengage อยู่ไหม · SRD: *"You can avoid provoking an Opportunity Attack by
    /// taking the Disengage action."* — ตัดสิทธิ์ **ทุกคน** ในการเคลื่อนที่ครั้งนั้น ไม่ใช่แค่คนแรก
    /// </param>
    /// <returns>
    /// จังหวะที่ต้องหยุดรอ reaction เรียงตาม <see cref="OpportunityAttackTrigger.LeavesAtStepIndex"/>
    /// จากน้อยไปมาก **ไม่ใช่ตามลำดับ <paramref name="threats"/>** เพราะชั้นบนต้องหยุดทีละจังหวะตาม
    /// ลำดับที่ผู้เดินผ่านจริง (คนที่อยู่ต้นทางต้องได้สวนก่อน) · ว่างเปล่า = เดินเส้นนี้ได้อย่างปลอดภัย
    /// </returns>
    /// <exception cref="ArgumentException">เส้นทางว่าง — ถือเป็น caller bug โยนทันทีตามแพทเทิร์น
    /// เดียวกับ resolver อื่นในโฟลเดอร์นี้ ไม่ใช่กลืนเงียบแล้วคืนลิสต์ว่างซึ่งอ่านไม่ออกว่าปลอดภัยจริง
    /// หรือส่งข้อมูลผิดมา</exception>
    public static IReadOnlyList<OpportunityAttackTrigger> Detect(
        IReadOnlyList<GridPoint> path,
        IReadOnlyList<ReachThreat> threats,
        bool moverIsDisengaging)
    {
        if (path.Count == 0)
        {
            throw new ArgumentException("เส้นทางห้ามว่าง", nameof(path));
        }

        var triggers = new List<OpportunityAttackTrigger>();

        // Disengage ตัดจบตั้งแต่ต้น ไม่ต้องไล่ใครเลย — เช็คนอกลูปเพราะมันเป็นคุณสมบัติของ "การเคลื่อนที่
        // ครั้งนี้" ไม่ใช่ของผู้ถือสิทธิ์รายตัว
        if (moverIsDisengaging)
        {
            return triggers;
        }

        foreach (var threat in threats)
        {
            // ตัดสิทธิ์รายตัวก่อนไล่เส้นทาง: มองไม่เห็นผู้เดิน หรือ reaction หมดแล้ว = ไม่มีทางได้สวน
            // ไม่ว่าเส้นทางจะเป็นอย่างไร
            if (!threat.CanSeeMover || !threat.ReactionAvailable)
            {
                continue;
            }

            for (var i = 1; i < path.Count; i++)
            {
                // trigger = ก้าวที่ "ช่องก่อนหน้ายังอยู่ในระยะ แต่ช่องที่กำลังเข้าไปหลุดระยะแล้ว"
                // เขียนตรงตามตัวบทคำว่า "leaves your reach" — การเดินเข้ามาใกล้แล้วหยุด หรือการเดินวน
                // อยู่ในระยะ ไม่เข้าเงื่อนไขนี้เลยโดยโครงสร้าง ไม่ต้องมีกฎแยกมากันอีก
                if (!IsWithinReach(path[i - 1], threat) || IsWithinReach(path[i], threat))
                {
                    continue;
                }

                triggers.Add(new OpportunityAttackTrigger(
                    threat.Id,
                    threat.Name,
                    i,
                    path[i - 1]));

                // ครั้งแรกที่หลุดระยะเท่านั้น — เลิกไล่เส้นทางของผู้ถือสิทธิ์รายนี้ทันที
                break;
            }
        }

        // เรียงตามก้าวที่หลุดจริง ไม่ใช่ตามลำดับ input · OrderBy ของ LINQ เป็น stable sort จึงคงลำดับ
        // เดิมของ threats ไว้เมื่อสองคนหลุดที่ก้าวเดียวกัน ทำให้ผลลัพธ์ deterministic
        return triggers.OrderBy(trigger => trigger.LeavesAtStepIndex).ToList();
    }

    /// <summary>
    /// ช่องนี้อยู่ในระยะเอื้อมของผู้ถือสิทธิ์ไหม — ระยะ Chebyshev แปลงเป็นฟุตแล้วเทียบแบบ **รวมค่าเท่า**
    /// (เอื้อม 10 ฟุตเอื้อมถึงช่องที่ห่าง 10 ฟุตพอดี)
    ///
    /// เป็น helper กลางที่เดียว เพราะเงื่อนไข trigger ต้องถามคำถามนี้กับทั้งช่องก่อนหน้าและช่องปลายทาง
    /// ของก้าวเดียวกัน ถ้าเขียนสูตรซ้ำสองที่แล้วแก้ไม่ครบ จะได้ trigger ที่หลุดระยะ "ครึ่งเดียว"
    /// </summary>
    private static bool IsWithinReach(GridPoint square, ReachThreat threat)
    {
        var chebyshev = Math.Max(
            Math.Abs(square.X - threat.Position.X),
            Math.Abs(square.Y - threat.Position.Y));

        return chebyshev * MovementCostResolver.FeetPerSquare <= threat.ReachFeet;
    }
}
