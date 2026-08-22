namespace DnDEz.Rules.Tests;

/// <summary>
/// HitPointResolver — temp HP ดูดซับ damage ก่อน HP จริงเสมอ, HP ห้ามติดลบ (clamp 0),
/// massive damage instant death, temp HP ไม่ stack (แทนที่ด้วยค่ามากกว่า), heal ปกติ+เกิน max
/// </summary>
public class HitPointResolverTests
{
    // ---- ApplyDamage: กรณีพื้นฐาน ไม่มี temp HP ----

    [Fact]
    public void ApplyDamage_NormalDamage_ReducesHpByAmount()
    {
        var result = HitPointResolver.ApplyDamage(currentHp: 20, maxHp: 30, currentTempHp: 0, damageAmount: 5);

        Assert.Equal(20, result.PreviousHp);
        Assert.Equal(15, result.NewHp);
        Assert.Equal(0, result.DamageAbsorbedByTempHp);
        Assert.Equal(5, result.DamageAppliedToHp);
        Assert.False(result.DroppedToZero);
        Assert.False(result.IsInstantDeath);
    }

    [Fact]
    public void ApplyDamage_HpNeverGoesNegative_ClampsAtZero()
    {
        var result = HitPointResolver.ApplyDamage(currentHp: 5, maxHp: 30, currentTempHp: 0, damageAmount: 100);

        Assert.Equal(0, result.NewHp);
        Assert.True(result.DroppedToZero);
    }

    [Fact]
    public void ApplyDamage_ExactlyReducesToZero_NoOverkill_NotInstantDeath()
    {
        var result = HitPointResolver.ApplyDamage(currentHp: 10, maxHp: 30, currentTempHp: 0, damageAmount: 10);

        Assert.Equal(0, result.NewHp);
        Assert.True(result.DroppedToZero);
        Assert.False(result.IsInstantDeath); // ไม่มีส่วนเกินเลย (excess = 0 < maxHp)
    }

    // ---- Massive damage instant death ----

    [Fact]
    public void ApplyDamage_ExcessMeetsMaxHp_IsInstantDeath()
    {
        // current=10, max=20 -> ลดถึง 0 แล้วส่วนเกิน 25 (35-10) >= maxHp(20) => ตายทันที
        var result = HitPointResolver.ApplyDamage(currentHp: 10, maxHp: 20, currentTempHp: 0, damageAmount: 35);

        Assert.Equal(0, result.NewHp);
        Assert.True(result.DroppedToZero);
        Assert.True(result.IsInstantDeath);
    }

    [Fact]
    public void ApplyDamage_ExcessExactlyEqualsMaxHp_IsInstantDeath()
    {
        // current=10, max=20, damage=30 -> excess = 30-10 = 20 == maxHp(20) => >= เข้าเงื่อนไข
        var result = HitPointResolver.ApplyDamage(currentHp: 10, maxHp: 20, currentTempHp: 0, damageAmount: 30);

        Assert.True(result.IsInstantDeath);
    }

    [Fact]
    public void ApplyDamage_ExcessJustBelowMaxHp_IsNotInstantDeath()
    {
        // current=10, max=20, damage=29 -> excess = 19 < maxHp(20)
        var result = HitPointResolver.ApplyDamage(currentHp: 10, maxHp: 20, currentTempHp: 0, damageAmount: 29);

        Assert.Equal(0, result.NewHp);
        Assert.True(result.DroppedToZero);
        Assert.False(result.IsInstantDeath);
    }

    [Fact]
    public void ApplyDamage_DoesNotDropToZero_NeverInstantDeath()
    {
        var result = HitPointResolver.ApplyDamage(currentHp: 50, maxHp: 20, currentTempHp: 0, damageAmount: 10);

        Assert.False(result.DroppedToZero);
        Assert.False(result.IsInstantDeath);
    }

    // ---- Temp HP ----

    [Fact]
    public void ApplyDamage_TempHpFullyAbsorbsDamage_RealHpUnaffected()
    {
        var result = HitPointResolver.ApplyDamage(currentHp: 10, maxHp: 20, currentTempHp: 5, damageAmount: 5);

        Assert.Equal(5, result.DamageAbsorbedByTempHp);
        Assert.Equal(0, result.NewTempHp);
        Assert.Equal(0, result.DamageAppliedToHp);
        Assert.Equal(10, result.NewHp); // HP จริงไม่โดนแตะเลย
        Assert.False(result.DroppedToZero);
    }

    [Fact]
    public void ApplyDamage_TempHpPartiallyAbsorbs_OverflowGoesToRealHp()
    {
        var result = HitPointResolver.ApplyDamage(currentHp: 10, maxHp: 20, currentTempHp: 5, damageAmount: 12);

        Assert.Equal(5, result.DamageAbsorbedByTempHp);
        Assert.Equal(0, result.NewTempHp);
        Assert.Equal(7, result.DamageAppliedToHp); // 12 - 5 ที่ tempHp ดูดไปแล้ว
        Assert.Equal(3, result.NewHp); // 10 - 7
    }

    [Fact]
    public void ApplyDamage_TempHpAbsorptionHappensBeforeInstantDeathCalculation()
    {
        // temp=5 ดูดซับ 5 จาก damage 40 -> damageToHp = 35 -> newHp=max(0,10-35)=0, excess=25>=maxHp(20)
        var result = HitPointResolver.ApplyDamage(currentHp: 10, maxHp: 20, currentTempHp: 5, damageAmount: 40);

        Assert.Equal(5, result.DamageAbsorbedByTempHp);
        Assert.Equal(0, result.NewTempHp);
        Assert.Equal(0, result.NewHp);
        Assert.True(result.IsInstantDeath);
    }

    [Fact]
    public void SetTempHp_IncomingHigherThanCurrent_ReplacesWithIncoming()
    {
        var result = HitPointResolver.SetTempHp(currentTempHp: 5, incomingTempHp: 10);

        Assert.Equal(10, result);
    }

    [Fact]
    public void SetTempHp_IncomingLowerThanCurrent_DoesNotStack_KeepsCurrent()
    {
        var result = HitPointResolver.SetTempHp(currentTempHp: 10, incomingTempHp: 5);

        Assert.Equal(10, result); // ไม่ใช่ 15 (ไม่ stack)
    }

    [Fact]
    public void SetTempHp_IncomingEqualsCurrent_StaysSame()
    {
        var result = HitPointResolver.SetTempHp(currentTempHp: 5, incomingTempHp: 5);

        Assert.Equal(5, result);
    }

    [Fact]
    public void SetTempHp_FromZero_TakesIncomingValue()
    {
        var result = HitPointResolver.SetTempHp(currentTempHp: 0, incomingTempHp: 8);

        Assert.Equal(8, result);
    }

    // ---- Healing ----

    [Fact]
    public void ApplyHealing_NormalHeal_AddsAmount()
    {
        var result = HitPointResolver.ApplyHealing(currentHp: 10, maxHp: 30, healAmount: 5);

        Assert.Equal(10, result.PreviousHp);
        Assert.Equal(15, result.NewHp);
        Assert.Equal(30, result.MaxHp);
    }

    [Fact]
    public void ApplyHealing_ExceedsMax_ClampsToMax()
    {
        var result = HitPointResolver.ApplyHealing(currentHp: 25, maxHp: 30, healAmount: 10);

        Assert.Equal(30, result.NewHp);
    }

    [Fact]
    public void ApplyHealing_FromZero_Works()
    {
        var result = HitPointResolver.ApplyHealing(currentHp: 0, maxHp: 30, healAmount: 1);

        Assert.Equal(1, result.NewHp);
    }

    [Fact]
    public void ApplyHealing_ZeroAmount_NoChange()
    {
        var result = HitPointResolver.ApplyHealing(currentHp: 10, maxHp: 30, healAmount: 0);

        Assert.Equal(10, result.NewHp);
    }

    [Fact]
    public void ApplyHealing_ExactlyToMax_NoClampNeededButCorrect()
    {
        var result = HitPointResolver.ApplyHealing(currentHp: 20, maxHp: 30, healAmount: 10);

        Assert.Equal(30, result.NewHp);
    }
}
