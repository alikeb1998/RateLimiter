-- rate_limit.lua
-- KEYS[1] = baseKey (e.g., "rl:global")
-- ARGV[1] = limit (max requests per window)
-- ARGV[2] = windowSeconds

local baseKey = KEYS[1]
local limit   = tonumber(ARGV[1])
local window  = tonumber(ARGV[2])

-- get current unix time from Redis itself
local t   = redis.call('TIME')       -- { seconds, microseconds }
local now = tonumber(t[1])

-- stable window id for this period
local windowId   = math.floor(now / window)
local counterKey = baseKey .. ":fw:" .. windowId

-- increment the counter for THIS window
local count = redis.call('INCR', counterKey)

-- only set TTL on first increment of the window
if count == 1 then
  redis.call('EXPIRE', counterKey, window)
end

-- allow up to 'limit' in this window
if count <= limit then
  return {1, count}
else
  return {0, count}
end
