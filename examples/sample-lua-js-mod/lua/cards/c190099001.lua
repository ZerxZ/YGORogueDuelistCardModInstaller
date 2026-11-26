-- 模组制作者的黄金之壶 (Modder's Golden Pot)
-- 演示 3 返回值 GetID()（管理器会自动升级 utility.lua 兼容）并调用 lua/lib 中的辅助函数
local s, id, o = GetID()

function s.initial_effect(c)
	SampleModHelper.RegisterDrawAndHealSpell(s, id, c, 2, 500)
end
