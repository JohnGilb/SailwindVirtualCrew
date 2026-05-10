# SailwindVirtualCrew

SailwindVirtualCrew adds crew as both an automation element and a new aspect of in-game progression to Sailwind. As part of your journey, you can collect and curate a crew to assist in managing your ship, your navigation, and quality of life.

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

### Crew Types

#### Deckhand

- Can operate winches.
- You can issue commands like:
  - "lower all sails"
- They take on the jobs, move to the winches (based on Dexterity), and operate them (based on Strength).
- If you have a big ship, you may want to get more than one deckhand.

#### Pilot

- Can operate the helm.
- You click on a virtual compass and they attempt to hold that heading.
- Pilots with more Intelligence do a better job of getting your requested heading just right.
- Their PID controls can be tuned by the player.
- They can also respond to simple commands like:
  - "Starboard 15 degrees"

#### Navigator

- Can operate navigation tools.

##### Quadrant
- Can only be used between 20:00-04:00 local time.
- Gets latitude.

##### Sun Compass
- Can only be used between 11:00-13:00 local time.
- Gets latitude.

##### Chronometer
- Can only be used between 11:00-13:00 Global time.
- Gets longitude.

##### Chronocompass
- Can be used between 08:00-16:00 local time.
- Gets longitude and latitude.

- Navigators with better Intelligence take more precise measurements.
- Special case:
  - Intelligence 1 navigators have basically no idea where they are.

---

## Planned Features

### Crew Management

- Crew can only be fired at ports.
  - You wouldn't just drop somebody in the ocean, would you?

### Crew (General)

- Crew should require wages, paid out periodically.
- Crew should require food, eaten daily.
- Crew will only operate if they have enough beds.

### Crew Roles

#### Pilots

- Should only be able to pilot for a duration based on their Constitution.
- If you want 24/7 automatic piloting, you'll probably need 2-3 pilots.

#### Navigators

- Should auto-detect what tools the player has nearby or in their personal inventory.
- Should only be able to take one "measurement" per tool type per day (or night, for the quadrant).
  - Represents their best effort using that tool.

#### Chief Officer

- New role.
- Meant to take some of your own command burden.
- Can only have 1.
- Can be configured to:
  - Periodically call for the sails to be trimmed.
  - Instruct the Navigator to take their measurements at the proper times.
  - Rotate out pilots as they become tired.
- Their Charisma acts as a buff (or detriment) to the statistics of other crew members.

#### Chef

- New role.
- Reduces how much food the crew eats by preparing correctly-sized meals.

#### Quartermaster

- New role.
- Helps move supplies around the boat.
- Activates/enables the CargoController mod.

#### Supercargo

- New role.
- Pays attention to the market and prices.
- Activates/enables the ProfitPercent mod.

### UI / UX

- For now, all features are accessed via windows.
- Long-term goal:
  - Achieve the same immersion Sailwind generally aims for.
  - Character models on board, etc.

---

## Known Issues

- When combined with the CargoController mod:
  - Mod text is all changed to Sailwind font.
  - Causes sizing and reflow issues.
- The following roles can still be hired but currently serve no function:
  - Chief Officer
  - Chef
  - Quartermaster
  - Supercargo
- Window sizes are wrong and initial placement is bad.

---

## Suspected Issues

- Very little testing has been done around adding/removing sails from the ship.
- Bugs are likely present there.

---

## How To Use This Mod

Press `B` to bring up the windows, and press `Tab` to free the mouse so you can interact with them.

### Sail Management

- Sails default to the name of the mast and the name of the sail.
  - This is difficult to read.
- You can rename sails to make quick access easier.
- Sails can be placed into groups so you can command several of them at once.

### Piloting

- Click on the heading you want on the compass diagram.
- Press `Start Piloting`.
- If you interact with the helm:
  - This should disengage the pilot.
- You can also stop piloting via the window.

### Navigation

- Checkbox any tools you have access to.
- Press the tool button to get a reading.

### Testing Mode

- In the Developer page:
  - Press `Access Developer powers`
- This enables:
  - 3 randomized deckhands
  - 1 navigator
  - 1 pilot
- Also enables various extra information on many panels.