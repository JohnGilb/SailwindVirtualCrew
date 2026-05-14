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
- Rename crew members
- Crew get tired and will need beds to sleep in.

### Crew Types

#### Deckhand

- Can operate sail winches (halyards/sheets).
- Can be commanded to bail out water
- Can raise and lower anchors
- You can issue commands like "lower all sails"
- They take on the jobs, move to the winches (based on Dexterity), and operate them (based on Strength).
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

Tools:
- Quadrant: Can only be used between 20:00-04:00 local time. Gets latitude.
- Sun Compass: Can only be used between 11:00-13:00 local time. Gets latitude.
- Chronometer: Can only be used between 11:00-13:00 Global time. Gets longitude.
- Chronocompass: Can be used between 08:00-16:00 local time. Gets longitude and latitude. Can be used more times per day.

#### Lookout

- Keeps an eye out for landmasses, based on their Wisdom, and reports the rough heading to them.
  - Can scan for and use spyglasses on board or in player inventory

#### Quartermaster
- Helps move supplies around the boat.
  - (Having one activates/enables the CargoController mod, if you have it installed)

#### Supercargo

- Pays attention to the market and prices.
  - (Having one activates/enables the ProfitPercent mod, if you have it installed)

---

## Planned Features

### Crew (General)

- Crew should require wages, paid out periodically.
  - Deckhands: 10 AA or equivalent daily? Missions pay out about 25 for a 1 day journey, and the player can start by taking 2 missions. 
    - If the player is making about 50 coins a day, then having 1 deckhand take 20% of that seems reasonable. 
    - With cost of food and water that's probably closer to 30% or more.
- Crew should require food, eaten daily.
  - Player needs about 8 units of water daily? And a barrel is about 20 coins and holds 60 units, so this also increasts costs by about 2.5 AA coins per day per crew

### Crew Roles

#### Deckhands

- Can operate bilge pumps
- Can repair ships with Oakum

#### Chief Officer

- New role.
- Meant to take some of your own command burden.
- Can only have 1.
- Can be configured to:
  - Periodically call for the sails to be trimmed.
  - Instruct the Navigator to take their measurements at the proper times.
  - Rotate out pilots as they become tired.
- Checks hull status and issues repairs at sea.
- Their Charisma acts as a buff (or detriment) to the statistics of other crew members.

#### Lookout

- Can collaborate with the Navigator to get the island name if you have a recent lat/lon and you've visited the goods trader on that island or have a chart with that island.
  - Not sure how the latter would work with scrambled seas...
  - Note that a bad lat/lon might mean they make a mistake and name it incorrectly!
  - Once you get very close to an island you've been to before, they'll identify it via its shape.
- Some way to "Announce" when land sighted when coming in from the ocean
- Maybe some kind of proximity warning for things right in front of you?

#### Chef

- New role.
- Reduces how much food the crew eats by preparing correctly-sized meals.

#### Quartermaster

- Manages repairs
- Can fill up a few barrels of water at larger ports from wells for free (not unlimited; at some point people don't let you hog all the water)

### UI / UX

- For now, all features are accessed via windows; long-term the goal would be to achieve the same immersion Sailwind generally aims for (character models on board)
- Dynamic hotkeys for combinations of sail groups & commands so you can shortcut your favorites (e.g. squares->deploy, jibs-> port1/2, etc

### Logic

- Improve sail auto-trim for sails to find an efficient point that also has some "safe distance" from any efficiency cliff.

---

## Known Issues

- The following roles can still be hired but currently serve no function:
  - Chief Officer
  - Chef
- Window sizes are wrong and initial placement is poor.
- Can see Water % even without Quartermaster

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