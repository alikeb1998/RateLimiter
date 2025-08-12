-- KEYS[1] = logKey
-- ARGV[1] = maxRequests
-- ARGV[2] = windowSeconds
-- ARGV[3] = now (optional; unix seconds)

local logKey = KEYS[1]
local maxReq = tonumber(ARGV[1])
local window = tonumber(ARGV[2])

-- robust now (fallback to Redis TIME)
local now = tonumber(ARGV[3])
if not now then
  local t = redis.call('TIME')
  now = tonumber(t[1])
end

-- 1) drop old entries
redis.call('ZREMRANGEBYSCORE', logKey, 0, now - window)

-- 2) count current hits
local count = redis.call('ZCARD', logKey)

-- 3) allow if under max, store a UNIQUE member
if count < maxReq then
  local seqKey = logKey .. ":seq"
  local seq = redis.call('INCR', seqKey)
  local member = tostring(now) .. ":" .. tostring(seq)

  redis.call('ZADD', logKey, now, member)
  redis.call('EXPIRE', logKey, window)
  redis.call('EXPIRE', seqKey, window)

  return {1, count + 1}
else
  return {0, count}
end
