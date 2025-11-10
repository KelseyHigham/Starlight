
function init()
  for r = 0, _rows-1 do
    print("row " .. r .. " has " .. columns(r) .. " columns")
  end
end




-- assumes center at (0,0)
function mycode(x,y)
  --return (math.abs(x) + math.abs(y))/50
  return 1-math.sqrt(x^2 + y^2)/50
end





function gamma(v)
  return v^2
end

function pixel(x, y)
  -- correct the fact that the x-coordinate is offset by the cutout
  tx = x + (34 - columns(y)) - 17
  ty = y - 30
  -- convert to diagonal coordinates
  dx = (ty + 2 * tx) / 2
  dy = (ty - 2 * tx) / 2
  return gamma(mycode(dx,dy))
end





--[[
function init()
  -- runs once
end

function pixel(x, y)
  local w = columns(y)
  local cx = w / 2
  local t = _time or 0
  -- simple moving pulse across each row
  local v = math.sin((x - cx) * 0.5 - t * 3.0)
  return (v + 1) * 0.5 -- normalized to 0..1
end
--]]




--[[
-- Selector: "plasma", "radial", "ripple", or "rain"
local effect = "ripple"

-- Utility helpers
local clamp = function(v, a, b) if v < a then return a elseif v > b then return b else return v end end
local lerp = function(a, b, t) return a + (b - a) * t end

-- PLASMA: layered sin waves (smooth, colorful when mapped to RGB; here grayscale)
local plasma = function(x, y)
  local w = columns(y)
  local nx = x / math.max(1, w)
  local ny = y / math.max(1, _rows)
  local v = 0
  v = v + math.sin((nx + _time * 0.6) * 6.0)
  v = v + math.sin((ny - _time * 0.8) * 5.0)
  v = v + math.sin((nx + ny + math.sin(_time * 0.4)) * 4.0)
  -- normalize from [-3,3] to [0,1]
  return ((v / 3 + 1) / 2)^2
end

-- RADIAL PULSE: moving center, pulse rings with falloff
local radial = function(x, y)
  local w = columns(y)
  local cx = w * 0.5 + math.sin(_time * 0.4) * (w * 0.25)
  local cy = _rows * 0.5 + math.cos(_time * 0.25) * (_rows * 0.2)
  local dx = x - cx
  local dy = y - cy
  local dist = math.sqrt(dx * dx + dy * dy)
  local maxd = math.sqrt((w*0.5)^2 + (_rows*0.5)^2)
  -- create a ring that moves outward
  local ring = 0.5 * (1 + math.cos(dist * 0.35 - _time * 3.0))
  -- attenuate by distance so rings fade out
  local falloff = clamp(1 - (dist / maxd), 0, 1)
  return clamp(ring * falloff, 0, 1)
end

-- RIPPLE: repeating expanding ripples from several centers
local ripple = function(x, y)
  local w = columns(y)
  local cx1 = w * 0.25
  local cy1 = _rows * 0.5
  local cx2 = w * 0.75
  local cy2 = _rows * 0.5
  local d1 = math.sqrt((x - cx1)^2 + (y - cy1)^2)
  local d2 = math.sqrt((x - cx2)^2 + (y - cy2)^2)
  local r1 = 0.5 * (1 + math.sin(d1 * 0.6 - _time * 4.0))
  local r2 = 0.5 * (1 + math.sin(d2 * 0.6 - _time * 3.2))
  -- combine and limit
  return clamp((r1 * (1 - d1/100) + r2 * (1 - d2/100)) * 0.7, 0, 1)
end

-- RAIN: falling drops per column (simple)
local rain = function(x, y)
  local w = columns(y)
  local speed = 6.0
  local phase = x * 0.6
  -- drop position floats down over time, wrap by _rows
  local dropPos = (_time * speed + phase) % (_rows + 4)
  local dist = math.abs(y - dropPos)
  -- narrow bright head plus trailing fade
  local head = clamp(1 - dist, 0, 1)
  local tail = clamp(1 - math.max(0, dist - 1) * 0.5, 0, 1) * 0.4
  return clamp(head + tail, 0, 1)
end

-- pixel() called by renderer; must return number in [0,1]
function pixel(x, y)
  if effect == "plasma" then
    return plasma(x, y)
  elseif effect == "radial" then
    return radial(x, y)
  elseif effect == "ripple" then
    return ripple(x, y)
  elseif effect == "rain" then
    return rain(x, y)
  else
    -- fallback: checker
    local cols = columns(y)
    local v = ((x + y) % 2) == 0 and 1 or 0
    return v
  end
end
--]]

--[[ -- pulse
function init()
  -- optional init code
end

function pixel(x, y)
  local t = _time or 0
  local cols = columns(y)
  local cx = cols / 2
  local v = math.sin(t * 2 + (x - cx) * 0.3 + y * 0.1)
  return (v + 1) / 2 -- value in 0..1
end
--]]



--[[ -- original
function __swipe_down(x, y)
  return (10 * math.cos((x / columns(y) + _time)) * math.sin(_time)) * _delta
end

function __anim2(x, y)
  
end

function pixel(x, y)
  local width = columns(y)
  
  local cx1 = math.sin(_time / 4) * width / 6 + width / 2
  local cy1 = math.sin(_time / 8) * _rows / 6 + _rows / 2
  local cx2 = math.cos(_time / 6) * width / 6 + width / 2
  local cy2 = math.cos(_time) * _rows / 6 + _rows / 2
  
  local dx = ((x - cx1) ^ 2) // 1
  local dy = ((y - cy1) ^ 2) // 1
  
  local dx2 = ((x - cx2) ^ 2) // 1
  local dy2 = ((y - cy2) ^ 2) // 1
  
  --return 10 * ((((math.sqrt(dx + dy) // 1) ~ (math.sqrt(dx2 + dy2) // 1)) >> 4) & 1) * _delta
  return __swipe_down(x, y)
end
--]]