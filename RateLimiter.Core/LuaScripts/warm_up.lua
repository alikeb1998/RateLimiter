-- Warm-up: initial fill
local bucketKey = KEYS[1]
local capacity = tonumber(ARGV[1])
redis.call("SETNX", bucketKey, capacity)
redis.call("SETNX", bucketKey .. ":ts", tonumber(ARGV[2]))
return 1
