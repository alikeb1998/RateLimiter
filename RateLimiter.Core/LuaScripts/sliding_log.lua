-- sliding_log.lua (Hybrid: token bucket + sliding log)
-- KEYS[1] = bucketKey
-- KEYS[2] = logKey
-- ARGV[1] = capacity
-- ARGV[2] = refillPerSec (int/float; floored per elapsed second)
-- ARGV[3] = periodSeconds
-- ARGV[4] = now (optional; unix seconds)

local bucketKey   = KEYS[1]
local logKey      = KEYS[2]
local cap         = tonumber(ARGV[1])
local refillRate  = tonumber(ARGV[2])
local period      = tonumber(ARGV[3])

-- robust 'now'
local now = tonumber(ARGV[4])
if not now then
  local t = redis.call('TIME')  -- {seconds, microseconds}
  now = tonumber(t[1])
end

-- token bucket refill
local tsKey   = bucketKey .. ':ts'
local tokens  = tonumber(redis.call('GET', bucketKey) or cap)
local last    = tonumber(redis.call('GET', tsKey) or now)
local elapsed = now - last
if elapsed < 0 then elapsed = 0 end

local add = math.floor(elapsed * refillRate)
tokens = math.min(cap, tokens + add)

-- sliding window cleanup
redis.call('ZREMRANGEBYSCORE', logKey, 0, now - period)
local count = redis.call('ZCARD', logKey)

local allowed = 0
if tokens > 0 and count < cap then
  tokens = tokens - 1

  -- UNIQUE member per hit even in the same second
  local seqKey = logKey .. ':seq'
  local seq = redis.call('INCR', seqKey)
  local member = tostring(now) .. ':' .. tostring(seq)

  redis.call('ZADD', logKey, now, member)
  redis.call('EXPIRE', logKey, period)
  redis.call('EXPIRE', seqKey, period)

  allowed = 1
end

-- persist bucket + ts and TTLs
redis.call('SET', bucketKey, tokens)
redis.call('SET', tsKey, now)
redis.call('EXPIRE', bucketKey, period)
redis.call('EXPIRE', tsKey, period)

-- return {allowed, newCount, tokensLeft}
if allowed == 1 then
  return {1, count + 1, tokens}
else
  return {0, count, tokens}
end
