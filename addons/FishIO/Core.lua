local ADDON, FIO = ...

---------------------------------------------------------
--  SECTION: Focus mode
---------------------------------------------------------
-- While a fishing pole is in the main hand (WoW Forever fishes with it there), these sound
-- CVars are set to the value beside them. Everything that makes noise without being part of
-- fishing goes quiet; effects (combat, interaction, the cast and the splash) stay on and go
-- to full volume. The values from before are saved first and put back when the pole comes off.
-- A CVar the client doesn't have is skipped.
FIO.FOCUS = {
    { cvar = "Sound_EnableMusic",       value = "0", what = "music" },
    { cvar = "Sound_EnableAmbience",    value = "0", what = "ambience" },
    { cvar = "Sound_EnableDialog",      value = "0", what = "NPC dialog" },
    { cvar = "Sound_EnableEmoteSounds", value = "0", what = "emote sounds" },
    { cvar = "Sound_EnablePetSounds",   value = "0", what = "pet sounds" },
    { cvar = "Sound_EnableErrorSpeech", value = "0", what = "error voice lines" },
    { cvar = "Sound_EnableSFX",         value = "1" },
    { cvar = "Sound_SFXVolume",         value = "1" },
    -- Keep playing when WoW isn't the window in front, so the splash is still heard (and IO Hub
    -- can still hear it) while you're looking at something else.
    { cvar = "Sound_EnableSoundWhenGameIsInBG", value = "1" },
}

local MAIN_HAND = INVSLOT_MAINHAND or 16
local WEAPON = (Enum and Enum.ItemClass and Enum.ItemClass.Weapon) or 2
local FISHING_POLE = (Enum and Enum.ItemWeaponSubclass and Enum.ItemWeaponSubclass.Fishingpole) or 20

local function P(...) print("|cff66ccffFishIO|r:", ...) end

local db
local active = false
local applying = false   -- FishIO is setting a CVar itself: CVAR_UPDATE isn't the player

---------------------------------------------------------
--  SECTION: Pole check
---------------------------------------------------------
function FIO.PoleInHand()
    local id = GetInventoryItemID("player", MAIN_HAND)
    if not id then return false end
    local _, _, _, _, _, classID, subClassID = C_Item.GetItemInfoInstant(id)
    return classID == WEAPON and subClassID == FISHING_POLE
end

---------------------------------------------------------
--  SECTION: Hold and put back
---------------------------------------------------------
local function Set(cvar, value)
    applying = true
    SetCVar(cvar, value)
    applying = false
end

-- Saves what each CVar is now (into the saved variables, so a crash or /reload mid-focus still
-- puts them back) and sets the focus values.
local function Apply()
    if active then return end
    db.held = db.held or {}
    local muted = {}
    for _, f in ipairs(FIO.FOCUS) do
        local now = GetCVar(f.cvar)
        if now ~= nil then
            if db.held[f.cvar] == nil then db.held[f.cvar] = now end
            if now ~= f.value then
                Set(f.cvar, f.value)
                if f.what then muted[#muted + 1] = f.what end
            end
        end
    end
    active = true
    if db.announce then
        P(#muted > 0 and ("focus on, " .. table.concat(muted, ", ") .. " off.") or "focus on.")
    end
end

-- Puts every held CVar back. Also runs at login for anything left over from a session that
-- ended without it (a crash, or the addon turned off mid-focus).
local function Restore(quiet)
    if db.held then
        for cvar, value in pairs(db.held) do Set(cvar, value) end
    end
    db.held = nil
    local was = active
    active = false
    if was and not quiet and db.announce then P("focus off, sound back to how it was.") end
end

function FIO.Update()
    if db.enabled and FIO.PoleInHand() then Apply() else Restore() end
end

---------------------------------------------------------
--  SECTION: Events
---------------------------------------------------------
local ev = CreateFrame("Frame")
ev:RegisterEvent("ADDON_LOADED")
ev:SetScript("OnEvent", function(_, event, arg1, arg2)
    if event == "ADDON_LOADED" then
        if arg1 ~= ADDON then return end
        FishIODB = FishIODB or {}
        db = FishIODB
        if db.enabled == nil then db.enabled = true end
        if db.announce == nil then db.announce = true end
        -- Left over from last time (a crash, or a /reload that skipped the logout): back first.
        Restore(true)
        ev:UnregisterEvent("ADDON_LOADED")
        ev:RegisterEvent("PLAYER_ENTERING_WORLD")
        ev:RegisterEvent("PLAYER_EQUIPMENT_CHANGED")
        ev:RegisterEvent("PLAYER_LOGOUT")
        ev:RegisterEvent("CVAR_UPDATE")
    elseif event == "PLAYER_ENTERING_WORLD" then
        FIO.Update()
    elseif event == "PLAYER_EQUIPMENT_CHANGED" then
        if arg1 == MAIN_HAND then FIO.Update() end
    elseif event == "PLAYER_LOGOUT" then
        -- The game saves CVars to Config.wtf on the way out: save the player's own values, not
        -- the focus ones. PLAYER_ENTERING_WORLD applies focus again after a /reload.
        Restore(true)
    elseif event == "CVAR_UPDATE" then
        -- The player changed a held setting themselves mid-focus (turned music back on, say):
        -- their choice stands and isn't put back afterwards.
        if applying or not active or not db.held then return end
        if db.held[arg1] ~= nil then db.held[arg1] = nil end
    end
end)

---------------------------------------------------------
--  SECTION: Slash command
---------------------------------------------------------
SLASH_FISHIO1 = "/fishio"
SlashCmdList.FISHIO = function(msg)
    local cmd = strtrim(msg or ""):lower()
    if cmd == "on" or cmd == "off" then
        db.enabled = cmd == "on"
        P(db.enabled and "on. Focus mode starts whenever a fishing pole is in your main hand." or "off.")
        FIO.Update()
    elseif cmd == "quiet" or cmd == "loud" then
        db.announce = cmd == "loud"
        P(db.announce and "chat messages on." or "no chat messages when focus starts or ends.")
    else
        local held = 0
        if db.held then for _ in pairs(db.held) do held = held + 1 end end
        P(("%s. Pole in hand: %s. Focus: %s%s."):format(
            db.enabled and "On" or "Off",
            FIO.PoleInHand() and "yes" or "no",
            active and "on" or "off",
            active and (" (%d sound settings held)"):format(held) or ""))
        P("/fishio on | off - focus mode with a pole in hand. /fishio quiet | loud - chat messages.")
    end
end
