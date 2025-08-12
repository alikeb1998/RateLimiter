-- KEYS[1] = bucketKey
-- KEYS[2] = tsKey
-- ARGV[1] = capacity
-- ARGV[2] = refillRatePerSec (float allowed)
-- ARGV[3] = ttlSeconds
-- ARGV[4] = now (optional, unix seconds)

local bucket = KEYS[1]
local tsKey  = KEYS[2]
local cap    = tonumber(ARGV[1])
local rate   = tonumber(ARGV[2])
local ttl    = tonumber(ARGV[3])

local now = tonumber(ARGV[4])
if not now then
  local t = redis.call('TIME'); now = tonumber(t[1])
end

local tokens = tonumber(redis.call('GET', bucket))
if not tokens then tokens = cap end

local last = tonumber(redis.call('GET', tsKey))
if not last then last = now end

local elapsed = now - last
if elapsed < 0 then elapsed = 0 end

-- integer refill
local add = math.floor(elapsed * rate)

-- only advance ts if we actually refilled
if add > 0 then
  tokens = math.min(cap, tokens + add)
  redis.call('SET', tsKey, now)
  redis.call('EXPIRE', tsKey, ttl)
end

if tokens > 0 then
  tokens = tokens - 1
  redis.call('SET', bucket, tokens)
  redis.call('EXPIRE', bucket, ttl)
  -- advance ts on consume as well
  redis.call('SET', tsKey, now)
  redis.call('EXPIRE', tsKey, ttl)
  return {1, tokens}
else
  -- persist bucket if we refilled (rare case tokens still 0)
  if add > 0 then
    redis.call('SET', bucket, tokens)
    redis.call('EXPIRE', bucket, ttl)
  end
  return {0, tokens}
end
 