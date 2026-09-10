namespace DnDEz.Rules.Tests;

/// <summary>
/// Sprint G Part 10 — ตรวจจับ opportunity attack จากเส้นทางเดิน (spatial-play-design §3.4)
///
/// **ตัวบท SRD 5.2 ฉบับ CC ที่เปิดอ่านจริงตอนเขียนเทสต์** (หมวด Combat "Opportunity Attacks"):
///   • *"You can make an Opportunity Attack when a creature that you can see leaves your reach.
///     To make the attack, take a Reaction to make one melee attack ... **The attack occurs right
///     before it leaves your reach.**"*
///   • *"You can avoid provoking an Opportunity Attack by taking the Disengage action. You also
///     don't provoke ... when you Teleport or when you are moved without using your movement..."*
///
/// สามจุดที่ตัวบทระบุชัดและเทสต์ในไฟล์นี้ล็อกไว้ เพราะเดาผิดได้ง่ายมาก:
///   1. trigger คือ **"ออกจาก" ระยะ** ไม่ใช่ "เข้ามาใน" ระยะ และไม่ใช่ "เดินอยู่ในระยะ"
///   2. การโจมตีเกิดที่ **ช่องสุดท้ายที่ยังอยู่ในระยะ** ไม่ใช่ช่องปลายทาง — สำคัญมากเพราะนี่คือ
///      จุดที่การเคลื่อนที่ต้อง "หยุดค้าง" ไว้รอ reaction (ถ้าหยุดผิดช่อง ระยะโจมตีจะผิดไปด้วย)
///   3. **"a creature that you can see"** — คนที่มองไม่เห็นผู้เคลื่อนที่ ไม่ได้สิทธิ์ OA
///      (ผูกกับหมอกของ Part 6 โดยตรง — ชั้นบนคำนวณ "เห็นไหม" มาให้แล้ว engine ไม่รู้จักหมอกเอง)
///
/// ระยะนับแบบ Chebyshev × 5 ฟุตต่อช่อง เลนส์เดียวกับ Part 8/9
///
/// **ไม่ตรวจความต่อเนื่องของเส้นทางที่นี่** — <see cref="MovementCostResolver.CostOfPath"/> โยน
/// ให้อยู่แล้วเมื่อก้าวไม่ติดกัน/ทะลุกำแพง หน้าที่ของไฟล์นี้คือกฎ OA ล้วน ๆ ไม่ทำงานซ้ำ · เส้นทางใน
/// เทสต์ทุกอันจึงเขียนให้ติดกันจริงตามที่ของจริงจะเป็น
/// </summary>
public class OpportunityAttackResolverTests
{
    private static readonly Guid Goblin = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
    private static readonly Guid Ogre = Guid.Parse("00000000-0000-0000-0000-0000000000c2");

    private static GridPoint P(int x, int y) => new(x, y);

    /// <summary>ศัตรูประชิดมาตรฐาน: เอื้อม 5 ฟุต มองเห็นผู้เคลื่อนที่ และยังไม่ได้ใช้ reaction</summary>
    private static ReachThreat Threat(Guid id, string name, GridPoint at, int reachFeet = 5) =>
        new(id, name, at, reachFeet, CanSeeMover: true, ReactionAvailable: true);

    // ── trigger พื้นฐาน ───────────────────────────────────────────────────────

    /// <summary>
    /// เดินออกจากระยะเอื้อมของก็อบลิน → เกิด OA
    ///
    /// ก็อบลินอยู่ (1,1) เอื้อม 5 ฟุต (= 1 ช่อง) · ผู้เล่นเดิน (0,0)→(1,0)→(2,0)→(3,0)
    /// สามช่องแรกยังห่างก็อบลิน 1 ช่อง (อยู่ในระยะ) พอถึง (3,0) ห่าง 2 ช่อง = 10 ฟุต **หลุดระยะ**
    /// การโจมตีจึงเกิดตอนอยู่ที่ (2,0) — ช่องสุดท้ายที่ยังอยู่ในระยะ ตามตัวบท
    /// *"The attack occurs right before it leaves your reach."*
    /// </summary>
    [Fact]
    public void Leaving_reach_triggers_an_opportunity_attack_from_the_last_square_still_in_reach()
    {
        var triggers = OpportunityAttackResolver.Detect(
            [P(0, 0), P(1, 0), P(2, 0), P(3, 0)],
            [Threat(Goblin, "ก็อบลิน", P(1, 1))],
            moverIsDisengaging: false);

        var trigger = Assert.Single(triggers);
        Assert.Equal(Goblin, trigger.ThreatId);
        Assert.Equal(P(2, 0), trigger.AttackFromSquare);
        Assert.Equal(3, trigger.LeavesAtStepIndex);
    }

    /// <summary>เดินเข้าหาศัตรูแล้วหยุดข้าง ๆ = ไม่เกิด OA — กฎผูกกับการ **ออกจาก** ระยะเท่านั้น
    /// (การเข้ามาใกล้ไม่ใช่ trigger ในกฎ 2024/SRD 5.2)</summary>
    [Fact]
    public void Moving_into_reach_and_stopping_does_not_trigger()
    {
        var triggers = OpportunityAttackResolver.Detect(
            [P(4, 0), P(3, 0), P(2, 0)],
            [Threat(Goblin, "ก็อบลิน", P(1, 0))],
            moverIsDisengaging: false);

        Assert.Empty(triggers);
    }

    /// <summary>เดินวนรอบ ๆ ศัตรูโดยไม่เคยหลุดระยะเลย = ไม่เกิด OA แม้จะเดินหลายช่อง</summary>
    [Fact]
    public void Moving_around_within_reach_never_triggers()
    {
        var triggers = OpportunityAttackResolver.Detect(
            [P(0, 0), P(1, 1), P(2, 1), P(2, 0)],
            [Threat(Goblin, "ก็อบลิน", P(1, 0))],
            moverIsDisengaging: false);

        Assert.Empty(triggers);
    }

    /// <summary>เริ่มนอกระยะและไม่เคยเข้าไปเลย = ไม่มีอะไรเกิดขึ้น</summary>
    [Fact]
    public void Path_that_never_enters_reach_does_not_trigger()
    {
        var triggers = OpportunityAttackResolver.Detect(
            [P(10, 10), P(11, 11), P(12, 12)],
            [Threat(Goblin, "ก็อบลิน", P(0, 0))],
            moverIsDisengaging: false);

        Assert.Empty(triggers);
    }

    /// <summary>ไม่ขยับเลย (เส้นทางช่องเดียว) ไม่มีทางเกิด OA</summary>
    [Fact]
    public void Standing_still_does_not_trigger()
    {
        var triggers = OpportunityAttackResolver.Detect(
            [P(0, 0)],
            [Threat(Goblin, "ก็อบลิน", P(1, 0))],
            moverIsDisengaging: false);

        Assert.Empty(triggers);
    }

    // ── ระยะเอื้อมที่ไม่ใช่ 5 ฟุต ────────────────────────────────────────────

    /// <summary>
    /// SRD: *"Certain creatures have melee attacks with a reach greater than 5 feet"* —
    /// ศัตรูสองตัวยืน**ช่องเดียวกัน**แต่เอื้อมต่างกัน ต้องหลุดระยะคนละจังหวะ
    ///
    /// เทสต์นี้กันการ hardcode 5 ฟุตไว้ใน engine ซึ่งจะทำให้ OA ของมอนสเตอร์ตัวใหญ่ผิดหมด
    /// </summary>
    [Fact]
    public void Reach_length_decides_when_the_trigger_fires()
    {
        var path = new[] { P(0, 0), P(1, 0), P(2, 0), P(3, 0) };

        var shortReach = OpportunityAttackResolver.Detect(
            path, [Threat(Goblin, "ก็อบลิน", P(0, 1))], moverIsDisengaging: false);
        var longReach = OpportunityAttackResolver.Detect(
            path, [Threat(Ogre, "ออร์ค", P(0, 1), reachFeet: 10)], moverIsDisengaging: false);

        // เอื้อม 5 ฟุต (1 ช่อง) → หลุดตอนถึง (2,0) โจมตีจาก (1,0)
        Assert.Equal(P(1, 0), Assert.Single(shortReach).AttackFromSquare);
        // เอื้อม 10 ฟุต (2 ช่อง) → หลุดตอนถึง (3,0) โจมตีจาก (2,0)
        Assert.Equal(P(2, 0), Assert.Single(longReach).AttackFromSquare);
    }

    // ── ข้อยกเว้นตามตัวบท ────────────────────────────────────────────────────

    /// <summary>SRD: *"You can avoid provoking an Opportunity Attack by taking the Disengage
    /// action."* — Disengage ตัดทุก trigger ของการเคลื่อนที่ครั้งนั้น ไม่ใช่แค่ตัวแรก</summary>
    [Fact]
    public void Disengage_suppresses_every_trigger_on_that_move()
    {
        var triggers = OpportunityAttackResolver.Detect(
            [P(0, 0), P(1, 1), P(2, 2), P(3, 3)],
            [Threat(Goblin, "ก็อบลิน", P(0, 1)), Threat(Ogre, "ออร์ค", P(1, 0))],
            moverIsDisengaging: true);

        Assert.Empty(triggers);
    }

    /// <summary>
    /// SRD: *"when a creature **that you can see** leaves your reach"* — ศัตรูที่มองไม่เห็น
    /// ผู้เคลื่อนที่ไม่ได้สิทธิ์ OA
    ///
    /// engine ไม่รู้จักหมอกเอง — ชั้นบนคำนวณ <c>CanSeeMover</c> มาให้ ตามหลักเดียวกับ Part 9
    /// ที่ความปลอดภัยมาจาก "รับอะไรเข้ามา" ไม่ใช่ "กรองอะไรออกไป"
    /// </summary>
    [Fact]
    public void Threat_that_cannot_see_the_mover_does_not_get_an_opportunity_attack()
    {
        var blind = new ReachThreat(
            Goblin, "ก็อบลินตาบอด", P(1, 1), ReachFeet: 5,
            CanSeeMover: false, ReactionAvailable: true);

        var triggers = OpportunityAttackResolver.Detect(
            [P(0, 0), P(1, 0), P(2, 0), P(3, 0)], [blind], moverIsDisengaging: false);

        Assert.Empty(triggers);
    }

    /// <summary>ใช้ reaction ไปแล้วในรอบนี้ = ทำ OA ไม่ได้ (SRD: OA "takes a Reaction")
    /// — และไม่ควรไปเด้ง prompt ให้คนที่ตอบอะไรไม่ได้อยู่แล้ว</summary>
    [Fact]
    public void Threat_with_no_reaction_left_does_not_trigger()
    {
        var spent = new ReachThreat(
            Goblin, "ก็อบลิน", P(1, 1), ReachFeet: 5,
            CanSeeMover: true, ReactionAvailable: false);

        var triggers = OpportunityAttackResolver.Detect(
            [P(0, 0), P(1, 0), P(2, 0), P(3, 0)], [spent], moverIsDisengaging: false);

        Assert.Empty(triggers);
    }

    // ── หลายศัตรูพร้อมกัน ────────────────────────────────────────────────────

    /// <summary>
    /// เดินผ่านศัตรูสองตัว → แต่ละตัวได้สิทธิ์ OA ของตัวเอง เรียงตามลำดับก้าวที่หลุดระยะ
    /// เพื่อให้ชั้นบนหยุดรอทีละจังหวะได้ถูกลำดับจริง (คนที่อยู่ต้นทางต้องได้สวนก่อน)
    ///
    /// ก็อบลิน (0,1) หลุดตอนถึง (2,0) · ออร์ค (3,1) หลุดตอนถึง (5,0)
    /// </summary>
    [Fact]
    public void Two_threats_left_at_different_steps_are_returned_in_step_order()
    {
        var triggers = OpportunityAttackResolver.Detect(
            [P(0, 0), P(1, 0), P(2, 0), P(3, 0), P(4, 0), P(5, 0)],
            [Threat(Ogre, "ออร์ค", P(3, 1)), Threat(Goblin, "ก็อบลิน", P(0, 1))],
            moverIsDisengaging: false);

        Assert.Equal(2, triggers.Count);
        // ส่งเข้ามาสลับลำดับโดยตั้งใจ — ผลลัพธ์ต้องเรียงตามก้าวที่หลุด ไม่ใช่ตามลำดับ input
        Assert.Equal(Goblin, triggers[0].ThreatId);
        Assert.Equal(Ogre, triggers[1].ThreatId);
        Assert.Equal(P(1, 0), triggers[0].AttackFromSquare);
        Assert.Equal(P(4, 0), triggers[1].AttackFromSquare);
    }

    /// <summary>
    /// ออกจากระยะ กลับเข้ามาใหม่ แล้วออกอีกครั้ง → ได้ OA **ครั้งเดียว**
    ///
    /// เพราะ OA กิน reaction และหนึ่งตัวมี reaction เดียวต่อรอบ — ถ้าคืน trigger ครั้งที่สองด้วย
    /// ชั้นบนจะเด้ง prompt ให้คนที่ไม่มี reaction เหลือแล้ว · เก็บเฉพาะครั้งแรกที่หลุดระยะ
    /// </summary>
    [Fact]
    public void Leaving_reentering_and_leaving_again_yields_only_one_trigger()
    {
        var triggers = OpportunityAttackResolver.Detect(
            [P(0, 0), P(1, 0), P(2, 0), P(1, 0), P(2, 0)],
            [Threat(Goblin, "ก็อบลิน", P(0, 1))],
            moverIsDisengaging: false);

        var trigger = Assert.Single(triggers);
        Assert.Equal(2, trigger.LeavesAtStepIndex);
        Assert.Equal(P(1, 0), trigger.AttackFromSquare);
    }

    /// <summary>ชื่อผู้ถือสิทธิ์ติดไปกับ trigger — ชั้นบนต้องเอาไปขึ้นคำเตือนให้ผู้เดินเห็น**ก่อนกด**
    /// ("เส้นทางนี้จะโดนก็อบลินยามสวน") โดยไม่ต้องไปค้นชื่อเองอีกรอบ (§3.4)</summary>
    [Fact]
    public void Trigger_carries_the_threat_name_for_the_warning_message()
    {
        var triggers = OpportunityAttackResolver.Detect(
            [P(0, 0), P(1, 0), P(2, 0), P(3, 0)],
            [Threat(Goblin, "ก็อบลินยาม", P(1, 1))],
            moverIsDisengaging: false);

        Assert.Equal("ก็อบลินยาม", Assert.Single(triggers).ThreatName);
    }

    // ── input ที่ผิดสัญญา ────────────────────────────────────────────────────

    [Fact]
    public void Empty_path_throws()
    {
        Assert.Throws<ArgumentException>(() => OpportunityAttackResolver.Detect(
            [], [Threat(Goblin, "ก็อบลิน", P(1, 0))], moverIsDisengaging: false));
    }
}
