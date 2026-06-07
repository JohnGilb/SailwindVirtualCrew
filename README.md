Contributors, please read the Contributor Guide: (<https://github.com/JohnGilb/SailwindVirtualCrew/blob/main/ContributorGuide.md>)

# SailwindVirtualCrew

SailwindVirtualCrew adds crew as both an automation element and a new aspect of in-game progression to Sailwind. As part of your journey, you can collect and curate a crew to assist in managing your ship, your navigation, and quality of life.

Depends on: 
- ModSaveBackups (<https://github.com/bryon82/ModSaveBackups>)

Interacts with these optional mods:
- CargoController (<https://github.com/JakeInABoat/SailwindCargoController>)
- ProfitPercent (<https://github.com/alesparise/Profit-Percent-Sailwind-Mod>)

Strongly inspired by:
- AutoSail (<https://github.com/kerseb/AutoSails>)
- AutoPilot (<https://github.com/kerseb/AutoPilot>)
- SailInfo (<https://github.com/alesparise/SailInfo-Sailwind-Mod>)

## Current Features

### Crew Management

- Crew can be hired/fired at ports.
  - Small ports have 1 crew.
  - Hub ports have 5 crew.
- Crew have statistics, but they often try to "sell themselves", so their true stats may be a little different than advertised.
  - You'll need to evaluate them yourself to see if they live up to their bluster.
- Stats range from 1 to 5 and use the classic:
  - Strength
  - Dexterity
  - Constitution
  - Intelligence
  - Wisdom
  - Charisma
- Crew members can be renamed and choose from a random NPC model
- Crew get tired and will need beds to sleep in. (on average 1 bed per 3 crew, but you may need slightly more if you work them hard)
- Crew require 10 Al'Ankh coins per day, though if you have none they don't care (for now).
- Each crewmember takes a share of your profits. Each deckhand, for example, will take 1% of your profit.
  - So if you buy a barrel of spices for 100 coins, and sell it for 300 coins, a deckhand will take 2 coins (1% of 200 profit).
  - They always take at least one coin.

### Crew Types

#### Deckhand

- Can operate sail winches (halyards/sheets).
- Can be commanded to bail out water
- Can raise and lower anchors
- Can throw mooring lines
- You can issue commands like "lower all sails" or "trim the jibs"
  - Dynamic hotkeys for combinations of sail groups & commands so you can shortcut your favorites (e.g. squares->deploy, jibs-> port1/2, etc
- They take on the jobs, move to their workstation (based on Dexterity), and operate them (based on Strength).
- If you have a big ship, you may want to get more than one deckhand, or you'll be waiting a long time for them to finish all the jobs!


#### Pilot

- Can operate the helm.
- You click on a virtual compass and they attempt to hold that heading.
- Pilots with more Intelligence do a better job of getting your requested heading correct.
- Their PID controls can be tuned by the player.
- They can also respond to simple commands like: "Starboard 15 degrees"

#### Navigator

- Can operate navigation tools.
  - Can scan for tools you have on the ship or in your inventory.
- Navigators with better Intelligence take more precise measurements.
  - Special case: Intelligence 1 navigators have basically no idea where they are.
- Can only operate most tools once per day, representing their "best effort" with it.
- Keep a record of the last 3 measurements

Tools:
- Quadrant: Can only be used between 20:00-04:00 local time. Gets latitude.
- Sun Compass: Can only be used between 11:00-13:00 local time. Gets latitude.
- Chronometer: Can only be used between 11:00-13:00 local time. Gets longitude.
- Chronocompass: Can be used between 08:00-16:00 local time. Gets longitude and latitude. Can be used more times per day.

#### Lookout

- Keeps an eye out for landmasses, and if they see one, reports the rough heading to them.
  - Uses Wisdom to determine how small a landmass they can see
  - Once they have a landmass in sight, they "build confidence" that it is in fact land. This speed is affected by dexterity (representing their ability to hold themselves & their tools precisely)
  - Once confidence is high enough, they announce the landmass and report heading. They also ring a bell that will wake a sleeping player.
  - Can scan for and use spyglasses on board or in player inventory

#### Quartermaster
- Helps move supplies around the boat.
  - Having one activates/enables the CargoController mod, if you have it installed
	- Additionally, the hotkey Z will grab the next "Port" cargo and place it directly in your hands.
- Will automatically fill up some water barrels for you at port when you visit the trader, based on Charisma.
  - Basically, how much water can they grab from the well/spring before the citizens start giving them the side-eye.

#### Supercargo

- Pays attention to the market and prices.
  - (Having one activates/enables the ProfitPercent mod, if you have it installed)
- Grants access to the "Sell" command for cargo on the boat if you are moored near a Port trader (X by default); deckhands will unload and sell cargo for you. 

#### First Officer

- Meant to take some of your own command burden. In a way, they help automate the mod itself.
- Can only have 1.
- Can set up a Day and Night shift for your crew.
  - Crew off-shift try to sleep if they are at least 80% tired, so they are fresh for their main shift.
  - If you have Lookouts/Pilots in both shifts, they will seamlessly transition and pass information to their successor.
- They instruct the Navigator (if available/awake) to take their measurements at the proper times.
- Can be configured to periodically call for the sails to be trimmed.
- Their Charisma acts as a buff (or detriment) to the statistics of other crew members.

---

## Planned Features

### Crew (General)

- Crew should require food/water, consumed daily.

### Crew Roles

#### Deckhands

- Can operate bilge pumps
- Can repair ships with Oakum
- Add "harden/ease" buttons to dual-sheet sails (Squares, jibs) for fine adjustment
- Can unmoor from the dock

#### Pilot

- Should only show Apparent Wind Angle if you have a wind compass

#### Lookout

- Can collaborate with the Navigator to get the island name if you have a recent lat/lon and you've visited the goods trader on that island or have a chart with that island.
  - Not sure how the latter would work with scrambled seas...
  - Note that a bad lat/lon might mean they make a mistake and name it incorrectly!
  - Once you get very close to an island you've been to before, they'll identify it via its shape.
- Should get a bonus to finding/identifying locations that are very close

#### Chef

- New role.
- Reduces how much food the crew eats by preparing correctly-sized meals.

#### Quartermaster

- Manages repairs

### UI / UX

- For now, most features are accessed via windows; long-term the goal would be to achieve the same immersion Sailwind generally aims for (character models on board)

---

## Known Issues

- The following roles can still be hired but currently serve no function:
  - Chef
- Window sizes are wrong and initial placement is poor.
- Can see Water % even without Quartermaster
- If your ship sinks, the code really bugs out. Don't sink your ship!
- Mooring range is too large.
- The Jong generally is a big buggy, but getting less so with every release.
- Documentation on how to mechanically use the features is bare
- Tasks started while you're on the boat can get real weird if you leave the boat.

---

## Suspected Issues

- Very little testing has been done around adding/removing sails from the ship.

---

## How To Use This Mod

Press `B` to bring up the windows, and press `Tab` to free the mouse so you can interact with them.

### Sail Management

- Sails default to the name of the mast and the name of the sail.
  - This is difficult to read, so you can rename sails to make quick access easier.
- Sails can be placed into groups so you can command several of them at once.

### Piloting

- Click on the heading you want on the compass diagram.
- Press `Start Piloting`.
- If you interact with the helm, this should disengage the pilot.
- You can also stop piloting via the window.

### Navigation

- Checkbox any tools you have access to.
- Press the tool button to get a reading.

### Testing Mode

- In the Developer page:
  - Press `Access Developer powers`
- This should have a button to add 3 randomized deckhands, 1 navigator, and 1 pilot to your ship.
- Also enables various extra information on many panels.
