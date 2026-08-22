namespace DnDEz.Rules.Tests;

/// <summary>
/// Action economy ต่อเทิร์น (player-side-design §4) — action/bonus action ใช้ได้ 1 ครั้งต่อเทิร์น,
/// reaction ใช้ได้ 1 ครั้งแต่ reset เฉพาะตอนขึ้นเทิร์นตัวเอง (ไม่ reset ตอนคนอื่นขึ้นเทิร์น),
/// movement ลดทีละส่วนได้ ไม่ใช่ all-or-nothing — ไม่มี RNG เกี่ยวข้องเลย เป็น resource tracking ล้วน ๆ
/// double-spend คือ caller bug ต้อง throw เหมือนแพทเทิร์นอื่นในระบบ ไม่ใช่ game outcome
/// </summary>
public class ActionEconomyResolverTests
{
    [Fact]
    public void StartTurn_AllResourcesAvailable_MovementFull()
    {
        var state = ActionEconomyResolver.StartTurn(maxMovementFeet: 30);

        Assert.True(state.ActionAvailable);
        Assert.True(state.BonusActionAvailable);
        Assert.True(state.ReactionAvailable);
        Assert.Equal(30, state.MovementRemainingFeet);
        Assert.Equal(30, state.MaxMovementFeet);
    }

    [Fact]
    public void StartTurn_ZeroMaxMovement_AllowedAndFullyRestricted()
    {
        // เช่นโดน restrained/grappled ที่ effect ทำ speed=0 — ไม่ควร throw แค่ movement ใช้ไม่ได้เลย
        var state = ActionEconomyResolver.StartTurn(maxMovementFeet: 0);

        Assert.Equal(0, state.MovementRemainingFeet);
        Assert.True(state.ActionAvailable);
    }

    [Fact]
    public void StartTurn_NegativeMaxMovement_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ActionEconomyResolver.StartTurn(maxMovementFeet: -5));
    }

    [Fact]
    public void StartTurn_ResetsPreviouslySpentResources()
    {
        var state = ActionEconomyResolver.StartTurn(maxMovementFeet: 30);
        state = ActionEconomyResolver.SpendAction(state);
        state = ActionEconomyResolver.SpendBonusAction(state);
        state = ActionEconomyResolver.SpendReaction(state);
        state = ActionEconomyResolver.SpendMovement(state, 30);

        var newTurnState = ActionEconomyResolver.StartTurn(maxMovementFeet: 30);

        Assert.True(newTurnState.ActionAvailable);
        Assert.True(newTurnState.BonusActionAvailable);
        Assert.True(newTurnState.ReactionAvailable);
        Assert.Equal(30, newTurnState.MovementRemainingFeet);
    }

    [Fact]
    public void StartTurn_WithDifferentSpeedThanPreviousTurn_UsesNewMax()
    {
        // speed เปลี่ยนได้ระหว่างเทิร์น (เช่นโดน haste/slow) — ต้องรับ max ใหม่ทุกครั้ง ไม่ cache เดิม
        var state = ActionEconomyResolver.StartTurn(maxMovementFeet: 60);

        Assert.Equal(60, state.MovementRemainingFeet);
        Assert.Equal(60, state.MaxMovementFeet);
    }

    // ---- Action ----

    [Fact]
    public void SpendAction_WhenAvailable_MarksUnavailable()
    {
        var state = ActionEconomyResolver.StartTurn(maxMovementFeet: 30);

        var result = ActionEconomyResolver.SpendAction(state);

        Assert.False(result.ActionAvailable);
    }

    [Fact]
    public void SpendAction_WhenAlreadyUsed_Throws()
    {
        var state = ActionEconomyResolver.StartTurn(maxMovementFeet: 30);
        state = ActionEconomyResolver.SpendAction(state);

        Assert.Throws<InvalidOperationException>(() => ActionEconomyResolver.SpendAction(state));
    }

    // ---- Bonus action ----

    [Fact]
    public void SpendBonusAction_WhenAvailable_MarksUnavailable()
    {
        var state = ActionEconomyResolver.StartTurn(maxMovementFeet: 30);

        var result = ActionEconomyResolver.SpendBonusAction(state);

        Assert.False(result.BonusActionAvailable);
    }

    [Fact]
    public void SpendBonusAction_WhenAlreadyUsed_Throws()
    {
        var state = ActionEconomyResolver.StartTurn(maxMovementFeet: 30);
        state = ActionEconomyResolver.SpendBonusAction(state);

        Assert.Throws<InvalidOperationException>(() => ActionEconomyResolver.SpendBonusAction(state));
    }

    [Fact]
    public void SpendAction_DoesNotAffectBonusActionAvailability()
    {
        var state = ActionEconomyResolver.StartTurn(maxMovementFeet: 30);

        var result = ActionEconomyResolver.SpendAction(state);

        Assert.True(result.BonusActionAvailable);
    }

    // ---- Reaction ----

    [Fact]
    public void SpendReaction_WhenAvailable_MarksUnavailable()
    {
        var state = ActionEconomyResolver.StartTurn(maxMovementFeet: 30);

        var result = ActionEconomyResolver.SpendReaction(state);

        Assert.False(result.ReactionAvailable);
    }

    [Fact]
    public void SpendReaction_WhenAlreadyUsed_Throws()
    {
        var state = ActionEconomyResolver.StartTurn(maxMovementFeet: 30);
        state = ActionEconomyResolver.SpendReaction(state);

        Assert.Throws<InvalidOperationException>(() => ActionEconomyResolver.SpendReaction(state));
    }

    [Fact]
    public void SpendReaction_StaysUnavailable_UntilOwnersNextStartTurn()
    {
        // จำลอง: ใช้ reaction ระหว่างเทิร์นคนอื่น -> ไม่มี method "EndTurn" ให้ reset -> ต้องรอ
        // StartTurn ของเจ้าของเองรอบหน้าเท่านั้นถึงจะกลับมาใช้ได้ (ไม่ reset ตอนคนอื่นขึ้นเทิร์น)
        var state = ActionEconomyResolver.StartTurn(maxMovementFeet: 30);
        state = ActionEconomyResolver.SpendReaction(state);

        // ใช้ resource อื่นระหว่างนั้นไม่ทำให้ reaction กลับมา
        state = ActionEconomyResolver.SpendAction(state);
        Assert.False(state.ReactionAvailable);

        var nextOwnTurn = ActionEconomyResolver.StartTurn(maxMovementFeet: 30);
        Assert.True(nextOwnTurn.ReactionAvailable);
    }

    // ---- Movement ----

    [Fact]
    public void SpendMovement_PartialSpend_DecrementsRemaining()
    {
        var state = ActionEconomyResolver.StartTurn(maxMovementFeet: 30);

        var result = ActionEconomyResolver.SpendMovement(state, 10);

        Assert.Equal(20, result.MovementRemainingFeet);
    }

    [Fact]
    public void SpendMovement_MultiplePartialSpends_AccumulateCorrectly()
    {
        var state = ActionEconomyResolver.StartTurn(maxMovementFeet: 30);

        state = ActionEconomyResolver.SpendMovement(state, 10);
        state = ActionEconomyResolver.SpendMovement(state, 5);
        state = ActionEconomyResolver.SpendMovement(state, 15);

        Assert.Equal(0, state.MovementRemainingFeet);
    }

    [Fact]
    public void SpendMovement_ExactRemaining_BringsToZero()
    {
        var state = ActionEconomyResolver.StartTurn(maxMovementFeet: 30);

        var result = ActionEconomyResolver.SpendMovement(state, 30);

        Assert.Equal(0, result.MovementRemainingFeet);
    }

    [Fact]
    public void SpendMovement_ExceedsRemaining_Throws()
    {
        var state = ActionEconomyResolver.StartTurn(maxMovementFeet: 30);
        state = ActionEconomyResolver.SpendMovement(state, 25);

        Assert.Throws<InvalidOperationException>(() => ActionEconomyResolver.SpendMovement(state, 10));
    }

    [Fact]
    public void SpendMovement_ExceedsFullRemainingInOneGo_Throws()
    {
        var state = ActionEconomyResolver.StartTurn(maxMovementFeet: 30);

        Assert.Throws<InvalidOperationException>(() => ActionEconomyResolver.SpendMovement(state, 31));
    }

    [Fact]
    public void SpendMovement_NegativeFeet_Throws()
    {
        var state = ActionEconomyResolver.StartTurn(maxMovementFeet: 30);

        Assert.Throws<ArgumentOutOfRangeException>(() => ActionEconomyResolver.SpendMovement(state, -5));
    }

    [Fact]
    public void SpendMovement_ZeroFeet_NoChange()
    {
        var state = ActionEconomyResolver.StartTurn(maxMovementFeet: 30);

        var result = ActionEconomyResolver.SpendMovement(state, 0);

        Assert.Equal(30, result.MovementRemainingFeet);
    }

    [Fact]
    public void SpendMovement_WhenAlreadyZero_ThrowsOnAnyFurtherSpend()
    {
        var state = ActionEconomyResolver.StartTurn(maxMovementFeet: 30);
        state = ActionEconomyResolver.SpendMovement(state, 30);

        Assert.Throws<InvalidOperationException>(() => ActionEconomyResolver.SpendMovement(state, 1));
    }
}
