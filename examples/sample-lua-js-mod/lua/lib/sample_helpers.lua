-- 示例 Lua 共享辅助库：会被管理器自动安全追加到 utility.lua 末尾
-- 多个 MOD 可以各自定义独立命名空间，避免全局函数名冲突。

SampleModHelper = SampleModHelper or {}

--- 为通常魔法卡快速注册「抽 N 张卡 + 回复 M 点基本分」效果（带卡名一回合一次）
---@param s table 卡片脚本表 (GetID 返回的第一个值)
---@param id number 卡密 (GetID 返回的第二个值)
---@param c Card 卡片实例
---@param drawCount number 抽卡张数
---@param recoverLp number 回复基本分数值
function SampleModHelper.RegisterDrawAndHealSpell(s, id, c, drawCount, recoverLp)
	local e1 = Effect.CreateEffect(c)
	e1:SetCategory(CATEGORY_DRAW + CATEGORY_RECOVER)
	e1:SetType(EFFECT_TYPE_ACTIVATE)
	e1:SetCode(EVENT_FREE_CHAIN)
	e1:SetCountLimit(1, id, EFFECT_COUNT_CODE_OATH)
	e1:SetTarget(function(e, tp, eg, ep, ev, re, r, rp, chk)
		if chk == 0 then
			return Duel.IsPlayerCanDraw(tp, drawCount)
		end
		Duel.SetTargetPlayer(tp)
		Duel.SetTargetParam(drawCount)
		Duel.SetOperationInfo(0, CATEGORY_DRAW, nil, 0, tp, drawCount)
		Duel.SetOperationInfo(0, CATEGORY_RECOVER, nil, 0, tp, recoverLp)
	end)
	e1:SetOperation(function(e, tp, eg, ep, ev, re, r, rp)
		local p, d = Duel.GetChainInfo(0, CHAININFO_TARGET_PLAYER, CHAININFO_TARGET_PARAM)
		if Duel.Draw(p, d, REASON_EFFECT) > 0 and recoverLp > 0 then
			Duel.BreakEffect()
			Duel.Recover(p, recoverLp, REASON_EFFECT)
		end
	end)
	c:RegisterEffect(e1)
end
