-- token_bucket.lua
-- KEYS[1] = bucketKey
-- KEYS[2] = tsKey
-- ARGV[1] = capacity
-- ARGV[2] = refillRate (tokens per second; can be fractional)
-- ARGV[3] = ttlSeconds  (how long to keep keys alive)
-- ARGV[4] = now         (unix seconds)

local bucketKey = KEYS[1]
local tsKey     = KEYS[2]
local capacity  = tonumber(ARGV[1])
local refillRate= tonumber(ARGV[2])
local ttl       = tonumber(ARGV[3])
local now       = tonumber(ARGV[4])

-- load state (default to full bucket on first use)
local tokens = redis.call('GET', bucketKey)
if not tokens then
  tokens = capacity
else
  tokens = tonumber(tokens)
end

local lastTs = tonumber(redis.call('GET', tsKey) or now)
local elapsed = now - lastTs
if elapsed < 0 then elapsed = 0 end

-- refill; allow fractional rate (use floor)
local refill = math.floor(elapsed * refillRate)
tokens = math.min(capacity, tokens + refill)

-- persist current time and set TTLs so bucket recovers after idle
redis.call('SET', tsKey, now)
redis.call('EXPIRE', tsKey, ttl)

if tokens > 0 then
  tokens = tokens - 1
  redis.call('SET', bucketKey, tokens)
  redis.call('EXPIRE', bucketKey, ttl)
  return {1, tokens}
else
  redis.call('SET', bucketKey, tokens)
  redis.call('EXPIRE', bucketKey, ttl)
  return {0, tokens}
end
