-- Triggers the action assigned to the first slot on the left panel
Macro(0)
-- Wait 3 seconds
sleep(3000)
-- Target self
TargetSelf()
-- Wait 1 second
sleep(1000)
-- Example of if condition
if tonumber(HEALTH) < tonumber(STR) then
	-- Say something
	Say("I need healing!")
else
	-- Say something
	Say("I am at full energy!")
end
-- And also print something here in the log
print("Finished")

