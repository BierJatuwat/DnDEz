using DnDEz.Rules.Tests.TestDoubles;

namespace DnDEz.Rules.Tests;

/// <summary>
/// Death save state machine (SRD 5.2) — d20 ไม่มี modifier ("1d20"), นับจาก "เลขดิบบน d20 ที่ถูก
/// เก็บจริง" เหมือนแพทเทิร์นของ AttackResolver: nat20=ฟื้นทันที (ไม่ใช่แค่ +1 success), nat1=นับ 2
/// failures, 10-19=success, 2-9=failure, สะสมครบ 3 success→Stable, ครบ 3 failure→Dead, clamp ที่ 3
/// เสมอ, advantage/disadvantage มีผลแค่เลือกลูกที่เก็บไม่ใช่กติกาพิเศษ
/// </summary>
public class DeathSaveResolverTests
{
    private static RollResult RollDeathSave(int rawValue, AdvantageMode advantage = AdvantageMode.None)
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(rawValue);
        return engine.RollSystem("1d20", roller, seed: 1, advantage: advantage);
    }

    // ---- Nat 20: ฟื้นทันที ----

    [Fact]
    public void Resolve_Nat20_RevivesImmediately_RegardlessOfCurrentCounts()
    {
        var roll = RollDeathSave(20);
        var currentState = new DeathSaveState(Successes: 1, Failures: 2, DeathSaveStatus.Rolling);

        var result = DeathSaveResolver.Resolve(roll, currentState);

        Assert.Equal(DeathSaveOutcome.Revived, result.Outcome);
        Assert.True(result.RevivedWithOneHp);
        Assert.Equal(0, result.NewState.Successes);
        Assert.Equal(0, result.NewState.Failures);
        Assert.NotEqual(DeathSaveStatus.Rolling, result.NewState.Status);
    }

    [Fact]
    public void Resolve_Nat20_DoesNotRequireThreeAccumulatedSuccesses()
    {
        // แม้ Successes=0 (ยังไม่เคยสำเร็จเลย) nat20 ก็ต้องจบกระบวนการทันที ไม่ใช่แค่ +1 success ธรรมดา
        var roll = RollDeathSave(20);
        var currentState = DeathSaveState.StartRolling;

        var result = DeathSaveResolver.Resolve(roll, currentState);

        Assert.Equal(DeathSaveOutcome.Revived, result.Outcome);
        Assert.NotEqual(DeathSaveStatus.Rolling, result.NewState.Status);
        Assert.NotEqual(DeathSaveStatus.Stable, result.NewState.Status); // ฟื้น != stable ธรรมดา
    }

    // ---- Nat 1: นับเป็น 2 failures ----

    [Fact]
    public void Resolve_Nat1_CountsAsTwoFailures()
    {
        // เริ่มจาก Successes=1 (ไม่ใช่ 0) เพื่อพิสูจน์ว่า nat1 ไม่แตะ/ไม่รีเซ็ต successes counter
        // จริง ๆ ถ้าเริ่มจาก 0 การ assert ว่ายังเป็น 0 จะไม่มีความหมาย (เป็นค่าเริ่มต้นอยู่แล้ว)
        var roll = RollDeathSave(1);
        var currentState = new DeathSaveState(Successes: 1, Failures: 0, DeathSaveStatus.Rolling);

        var result = DeathSaveResolver.Resolve(roll, currentState);

        Assert.Equal(DeathSaveOutcome.CriticalFailure, result.Outcome);
        Assert.Equal(2, result.NewState.Failures);
        Assert.Equal(1, result.NewState.Successes); // ไม่ถูกแตะ ยังคงค่าเดิม
        Assert.Equal(DeathSaveStatus.Rolling, result.NewState.Status); // ยังไม่ครบ 3
    }

    [Fact]
    public void Resolve_Nat1WhenFailuresAtTwo_ClampsAtThreeAndBecomesDead()
    {
        // 2 + 2 = 4 ต้อง clamp ที่ 3 ไม่ใช่ปล่อยเกิน
        var roll = RollDeathSave(1);
        var currentState = new DeathSaveState(Successes: 0, Failures: 2, DeathSaveStatus.Rolling);

        var result = DeathSaveResolver.Resolve(roll, currentState);

        Assert.Equal(3, result.NewState.Failures);
        Assert.Equal(DeathSaveStatus.Dead, result.NewState.Status);
    }

    // ---- 10-19: success ----

    [Theory]
    [InlineData(10)]
    [InlineData(15)]
    [InlineData(19)]
    public void Resolve_RollBetween10And19_CountsAsOneSuccess(int rawValue)
    {
        var roll = RollDeathSave(rawValue);
        var currentState = DeathSaveState.StartRolling;

        var result = DeathSaveResolver.Resolve(roll, currentState);

        Assert.Equal(DeathSaveOutcome.Success, result.Outcome);
        Assert.Equal(1, result.NewState.Successes);
        Assert.Equal(0, result.NewState.Failures);
        Assert.Equal(DeathSaveStatus.Rolling, result.NewState.Status);
    }

    // ---- 2-9: failure ----

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(9)]
    public void Resolve_RollBetween2And9_CountsAsOneFailure(int rawValue)
    {
        var roll = RollDeathSave(rawValue);
        var currentState = DeathSaveState.StartRolling;

        var result = DeathSaveResolver.Resolve(roll, currentState);

        Assert.Equal(DeathSaveOutcome.Failure, result.Outcome);
        Assert.Equal(1, result.NewState.Failures);
        Assert.Equal(0, result.NewState.Successes);
        Assert.Equal(DeathSaveStatus.Rolling, result.NewState.Status);
    }

    // ---- สะสมครบ 3 ----

    [Fact]
    public void Resolve_ThirdSuccess_BecomesStableAndStopsRolling()
    {
        var roll = RollDeathSave(15);
        var currentState = new DeathSaveState(Successes: 2, Failures: 1, DeathSaveStatus.Rolling);

        var result = DeathSaveResolver.Resolve(roll, currentState);

        Assert.Equal(3, result.NewState.Successes);
        Assert.Equal(DeathSaveStatus.Stable, result.NewState.Status);
    }

    [Fact]
    public void Resolve_ThirdFailure_BecomesDead()
    {
        var roll = RollDeathSave(5);
        var currentState = new DeathSaveState(Successes: 1, Failures: 2, DeathSaveStatus.Rolling);

        var result = DeathSaveResolver.Resolve(roll, currentState);

        Assert.Equal(3, result.NewState.Failures);
        Assert.Equal(DeathSaveStatus.Dead, result.NewState.Status);
    }

    [Fact]
    public void Resolve_WhenAlreadyStableWithThreeSuccesses_Throws()
    {
        // ชื่อเดิมสื่อผิดว่าทดสอบ clamp arithmetic ของ successes แต่จริง ๆ แล้ว currentState เป็น
        // Stable อยู่แล้วก่อนเรียก ทำให้ status-guard (ต้อง Rolling เท่านั้น) throw ก่อนที่ AddSuccesses
        // จะถูกเรียกเลยด้วยซ้ำ — เปลี่ยนชื่อให้ตรงกับสิ่งที่ทดสอบจริง (guard, ไม่ใช่ clamp)
        var roll = RollDeathSave(15);
        var currentState = new DeathSaveState(Successes: 3, Failures: 0, DeathSaveStatus.Stable);

        Assert.Throws<InvalidOperationException>(() => DeathSaveResolver.Resolve(roll, currentState));
    }

    // ---- Advantage/Disadvantage: มีผลแค่เลือกลูกที่เก็บ ไม่ใช่กติกาพิเศษของ death save ----

    [Fact]
    public void Resolve_Advantage_KeptNat20_RevivesImmediately()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(3, 20); // advantage เก็บสูงสุด = 20
        var roll = engine.RollSystem("1d20", roller, seed: 1, advantage: AdvantageMode.Advantage);
        var currentState = DeathSaveState.StartRolling;

        var result = DeathSaveResolver.Resolve(roll, currentState);

        Assert.Equal(DeathSaveOutcome.Revived, result.Outcome);
    }

    [Fact]
    public void Resolve_Disadvantage_DroppedNat20DoesNotCountAsRevive()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(20, 3); // disadvantage เก็บต่ำสุด = 3, drop 20 ทิ้ง
        var roll = engine.RollSystem("1d20", roller, seed: 1, advantage: AdvantageMode.Disadvantage);
        var currentState = DeathSaveState.StartRolling;

        var result = DeathSaveResolver.Resolve(roll, currentState);

        // เก็บ 3 → failure ธรรมดา ไม่ใช่ revive แม้เลข 20 จะโผล่ในผลทอย (ถูก drop)
        Assert.Equal(DeathSaveOutcome.Failure, result.Outcome);
        Assert.Equal(1, result.NewState.Failures);
    }

    [Fact]
    public void Resolve_Disadvantage_KeptNat1CountsAsTwoFailures()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(15, 1); // disadvantage เก็บต่ำสุด = 1
        var roll = engine.RollSystem("1d20", roller, seed: 1, advantage: AdvantageMode.Disadvantage);
        var currentState = DeathSaveState.StartRolling;

        var result = DeathSaveResolver.Resolve(roll, currentState);

        Assert.Equal(DeathSaveOutcome.CriticalFailure, result.Outcome);
        Assert.Equal(2, result.NewState.Failures);
    }

    // ---- Guard: หา d20 เดี่ยวที่ไม่ถูก drop ----

    [Fact]
    public void Resolve_RollWithoutD20_ThrowsInvalidOperationException()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(3, 4);
        var roll = engine.RollSystem("2d6", roller, seed: 1);

        Assert.Throws<InvalidOperationException>(
            () => DeathSaveResolver.Resolve(roll, DeathSaveState.StartRolling));
    }

    [Fact]
    public void Resolve_RollWithMultipleKeptD20s_ThrowsInvalidOperationException()
    {
        var engine = new DiceEngine();
        var roller = new SequenceDiceRoller(5, 6);
        var roll = engine.RollSystem("2d20", roller, seed: 1); // ไม่มี kh/kl -> ทั้งคู่ไม่ถูก drop

        Assert.Throws<InvalidOperationException>(
            () => DeathSaveResolver.Resolve(roll, DeathSaveState.StartRolling));
    }

    // ---- Guard: ต้องอยู่สถานะ Rolling เท่านั้นถึงจะทอยได้ ----

    [Fact]
    public void Resolve_WhenStatusIsStable_Throws()
    {
        var roll = RollDeathSave(15);
        var currentState = new DeathSaveState(3, 0, DeathSaveStatus.Stable);

        Assert.Throws<InvalidOperationException>(() => DeathSaveResolver.Resolve(roll, currentState));
    }

    [Fact]
    public void Resolve_WhenStatusIsDead_Throws()
    {
        var roll = RollDeathSave(15);
        var currentState = new DeathSaveState(0, 3, DeathSaveStatus.Dead);

        Assert.Throws<InvalidOperationException>(() => DeathSaveResolver.Resolve(roll, currentState));
    }

    [Fact]
    public void Resolve_WhenStatusIsNotDying_Throws()
    {
        var roll = RollDeathSave(15);

        Assert.Throws<InvalidOperationException>(() => DeathSaveResolver.Resolve(roll, DeathSaveState.NotDying));
    }

    // ---- โดนโจมตีตอน 0 HP (event แยกจากการทอย ไม่ใช่การทอยลูกเต๋า) ----

    [Fact]
    public void ApplyAttackedAtZeroHp_NormalHit_AddsOneFailure()
    {
        var currentState = DeathSaveState.StartRolling;

        var result = DeathSaveResolver.ApplyAttackedAtZeroHp(currentState, isCriticalHit: false);

        Assert.Equal(1, result.NewState.Failures);
        Assert.Equal(DeathSaveStatus.Rolling, result.NewState.Status);
        Assert.False(result.WasCriticalHit);
        Assert.Equal(1, result.FailuresApplied);
    }

    [Fact]
    public void ApplyAttackedAtZeroHp_CriticalHit_AddsTwoFailures()
    {
        var currentState = DeathSaveState.StartRolling;

        var result = DeathSaveResolver.ApplyAttackedAtZeroHp(currentState, isCriticalHit: true);

        Assert.Equal(2, result.NewState.Failures);
        Assert.True(result.WasCriticalHit);
        Assert.Equal(2, result.FailuresApplied);
    }

    [Fact]
    public void ApplyAttackedAtZeroHp_AccumulatesToThree_BecomesDead()
    {
        var currentState = new DeathSaveState(Successes: 1, Failures: 2, DeathSaveStatus.Rolling);

        var result = DeathSaveResolver.ApplyAttackedAtZeroHp(currentState, isCriticalHit: false);

        Assert.Equal(3, result.NewState.Failures);
        Assert.Equal(DeathSaveStatus.Dead, result.NewState.Status);
    }

    [Fact]
    public void ApplyAttackedAtZeroHp_CriticalHitClampsAtThree()
    {
        var currentState = new DeathSaveState(Successes: 0, Failures: 2, DeathSaveStatus.Rolling);

        var result = DeathSaveResolver.ApplyAttackedAtZeroHp(currentState, isCriticalHit: true);

        Assert.Equal(3, result.NewState.Failures); // 2 + 2 = 4 -> clamp 3
        Assert.Equal(DeathSaveStatus.Dead, result.NewState.Status);
    }

    [Fact]
    public void ApplyAttackedAtZeroHp_WhenNotRolling_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => DeathSaveResolver.ApplyAttackedAtZeroHp(DeathSaveState.NotDying, isCriticalHit: false));
    }

    [Fact]
    public void ApplyAttackedAtZeroHp_WhenAlreadyDead_Throws()
    {
        // เดิมมีแค่ NotDying/Stable ครอบ "ไม่ใช่ Rolling" ยังขาด Dead — Resolve ครอบครบทั้ง 3 แล้ว
        // เพิ่มเคสนี้ให้สมมาตรกัน แม้ guard เป็นเงื่อนไขเดียวกัน (!= Rolling) ก็ตาม
        var currentState = new DeathSaveState(0, 3, DeathSaveStatus.Dead);

        Assert.Throws<InvalidOperationException>(
            () => DeathSaveResolver.ApplyAttackedAtZeroHp(currentState, isCriticalHit: false));
    }

    [Fact]
    public void ApplyAttackedAtZeroHp_WhenAlreadyStable_Throws()
    {
        var currentState = new DeathSaveState(3, 0, DeathSaveStatus.Stable);

        Assert.Throws<InvalidOperationException>(
            () => DeathSaveResolver.ApplyAttackedAtZeroHp(currentState, isCriticalHit: false));
    }

    // ---- Reset helper ----

    [Fact]
    public void NotDying_IsZeroedAndNotInRollingStatus()
    {
        var state = DeathSaveState.NotDying;

        Assert.Equal(0, state.Successes);
        Assert.Equal(0, state.Failures);
        Assert.NotEqual(DeathSaveStatus.Rolling, state.Status);
        Assert.NotEqual(DeathSaveStatus.Dead, state.Status);
        Assert.NotEqual(DeathSaveStatus.Stable, state.Status);
    }

    [Fact]
    public void StartRolling_IsZeroedAndInRollingStatus()
    {
        var state = DeathSaveState.StartRolling;

        Assert.Equal(0, state.Successes);
        Assert.Equal(0, state.Failures);
        Assert.Equal(DeathSaveStatus.Rolling, state.Status);
    }
}
