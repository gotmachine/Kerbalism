﻿using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;


namespace KERBALISM {


  public sealed class Habitat : PartModule, ISpecifics, IConfigurable
  {
    // config
    [KSPField] public double volume = 0.0;            // habitable volume in m^3, deduced from bounding box if not specified
    [KSPField] public double surface = 0.0;           // external surface in m^2, deduced from bounding box if not specified
    [KSPField] public double heat_capacity = 0.0;     // habitat stored heat in watts at 295K, deduced from settings value if not specified
    [KSPField] public string inflate = string.Empty;  // inflate animation, if any
    [KSPField] public bool   toggle = true;           // show the enable/disable toggle

    // persistence
    [KSPField(isPersistant = true)] public State state = State.enabled;

    // rmb ui status strings
    [KSPField(guiActive = false, guiActiveEditor = true, guiName = "Volume")] public string Volume;
    [KSPField(guiActive = false, guiActiveEditor = true, guiName = "Surface")] public string Surface;
    [KSPField(guiActive = true, guiActiveEditor = false, guiName = "atmo flux   (W)", guiFormat = "F1")] public double atmoflux;
    [KSPField(guiActive = true, guiActiveEditor = false, guiName = "env flux    (W)", guiFormat = "F1")] public double envflux;
    [KSPField(guiActive = true, guiActiveEditor = false, guiName = "kerbal flux (W)", guiFormat = "F1")] public double kerbalflux;
    [KSPField(guiActive = true, guiActiveEditor = false, guiName = "net flux    (W)", guiFormat = "F1")] public double netflux;

    // animations
    Animator inflate_anim;

    // pseudo-ctor
    public override void OnStart(StartState state)
    {
      // don't break tutorial scenarios
      if (Lib.DisableScenario(this)) return;

      // calculate habitat internal volume
      if (volume <= double.Epsilon) volume = Lib.PartVolume(part);

      // calculate habitat external surface
      if (surface <= double.Epsilon) surface = Lib.PartSurface(part);

      // calculate habitat heat capacity
      if (heat_capacity <= double.Epsilon) heat_capacity = Settings.HabSpecificHeat * volume * Settings.TemperatureThreshold * 2.5;

      // set RMB UI status strings
      Volume = Lib.HumanReadableVolume(volume);
      Surface = Lib.HumanReadableSurface(surface);

      // hide toggle if specified
      Events["Toggle"].active = toggle;
      Actions["Action"].active = toggle;

      // create animators
      inflate_anim = new Animator(part, inflate);

      // configure on start
      Configure(true);
    }

    public void Configure(bool enable)
    {
      if (enable)
      {
        // if never set, this is the case if:
        // - part is added in the editor
        // - module is configured first time either in editor or in flight
        // - module is added to an existing savegame
        if (!part.Resources.Contains("Atmosphere"))
        {
          // add internal atmosphere resources
          // - disabled habitats start with zero atmosphere
          Lib.AddResource(part, "Atmosphere", (state == State.enabled && Features.Pressure) ? volume : 0.0, volume);
          Lib.AddResource(part, "WasteAtmosphere", 0.0, volume);

          // add heat resources to reach ideal temperature
          Lib.AddResource(part, "PosWatts", heat_capacity, heat_capacity);
          Lib.AddResource(part, "NegWatts", heat_capacity, heat_capacity);

          // add external surface shielding
          Lib.AddResource(part, "Shielding", 0.0, surface);

          // inflatable habitats can't be shielded (but still need the capacity)
          part.Resources["Shielding"].isTweakable = inflate.Length == 0;

          // if shielding feature is disabled, just hide it
          part.Resources["Shielding"].isVisible = Features.Shielding;
        }
      }
      else
      {
        Lib.RemoveResource(part, "Atmosphere", 0.0, volume);
        Lib.RemoveResource(part, "WasteAtmosphere", 0.0, volume);
        Lib.RemoveResource(part, "Shielding", 0.0, surface);
        Lib.RemoveResource(part, "PosWatts", 0.0, 0.0);
        Lib.RemoveResource(part, "NegWatts", 0.0, 0.0);
      }
    }


    void set_flow(bool b)
    {
      Lib.SetResourceFlow(part, "Atmosphere", b);
      Lib.SetResourceFlow(part, "WasteAtmosphere", b);
      Lib.SetResourceFlow(part, "Shielding", b);
      Lib.SetResourceFlow(part, "PosWatts", b);
      Lib.SetResourceFlow(part, "NegWatts", b);
    }


    State equalize()
    {
      // in flight
      if (Lib.IsFlight())
      {
        // shortcuts
        resource_info vessel_atmo = ResourceCache.Info(vessel, "Atmosphere");
        PartResource hab_atmo = part.Resources["Atmosphere"];

        // get level of atmosphere in vessel and part
        double vessel_level = vessel_atmo.level;
        double hab_level = Lib.Level(part, "Atmosphere", true);

        // equalization succeeded if the levels are the same
        // note: this behave correctly in the case the hab is the only enabled one or not
        if (Math.Abs(vessel_level - hab_level) < 0.01) return State.enabled;

        // in case vessel pressure is dropping during equalization, it mean that pressure
        // control is not enough so we just enable the hab while not fully equalized
        if (vessel_atmo.rate < 0.0)
        {
          return State.enabled;
        }

        // determine equalization speed
        // we deal with the case where a big hab is sucking all atmosphere from the rest of the vessel
        double amount = Math.Min(Cache.VesselInfo(vessel).volume, volume) * equalize_speed * Kerbalism.elapsed_s;

        // vessel pressure is higher
        if (vessel_level > hab_level)
        {
          // clamp amount to what's available in the vessel and what can fit in the part
          amount = Math.Min(amount, vessel_atmo.amount);
          amount = Math.Min(amount, hab_atmo.maxAmount - hab_atmo.amount);

          // consume from all enabled habs in the vessel
          vessel_atmo.Consume(amount);

          // produce in the part
          hab_atmo.amount += amount;
        }
        // vessel pressure is lower
        else
        {
          // consume from the part, clamp amount to what's available in the part
          amount = Math.Min(amount, hab_atmo.amount);
          hab_atmo.amount -= amount;

          // produce in all enabled habs in the vessel
          // (attempt recovery, but dump overboard if there is no capacity left)
          vessel_atmo.Produce(amount);
        }

        // equalization still in progress
        return State.equalizing;
      }
      // in the editors
      else
      {
        // set amount to max capacity
        PartResource hab_atmo = part.Resources["Atmosphere"];
        hab_atmo.amount = hab_atmo.maxAmount;

        // return new state
        return State.enabled;
      }
    }


    State venting()
    {
      // in flight
      if (Lib.IsFlight())
      {
        // shortcuts
        PartResource atmo = part.Resources["Atmosphere"];
        PartResource waste = part.Resources["WasteAtmosphere"];

        // venting succeeded if the amount reached zero
        if (atmo.amount <= double.Epsilon && waste.amount <= double.Epsilon) return State.disabled;

        // how much to vent
        double rate = volume * equalize_speed * Kerbalism.elapsed_s;
        double atmo_k = atmo.amount / (atmo.amount + waste.amount);
        double waste_k = waste.amount / (atmo.amount + waste.amount);

        // consume from the part, clamp amount to what's available
        atmo.amount = Math.Max(atmo.amount - rate * atmo_k, 0.0);
        waste.amount = Math.Max(waste.amount - rate * waste_k, 0.0);

        // venting still in progress
        return State.venting;
      }
      // in the editors
      else
      {
        // set amount to zero
        part.Resources["Atmosphere"].amount = 0.0;
        part.Resources["WasteAtmosphere"].amount = 0.0;

        // return new state
        return State.disabled;
      }
    }


    public void Update()
    {
      // update ui
      string status_str = string.Empty;
      switch(state)
      {
        case State.enabled:     status_str = "enabled"; break;
        case State.disabled:    status_str = "disabled"; break;
        case State.equalizing:  status_str = inflate.Length == 0 ? "equalizing..." : "inflating..."; break;
        case State.venting:     status_str = inflate.Length == 0 ? "venting..." : "deflating..."; break;
      }
      Events["Toggle"].guiName = Lib.StatusToggle("Habitat", status_str);

      // if there is an inflate animation, set still animation from pressure
      inflate_anim.still(Lib.Level(part, "Atmosphere", true));
    }


    public void FixedUpdate()
    {

      if (Lib.IsFlight())
      {
        atmoflux = Cache.VesselInfo(vessel).hab_atmo_flux;
        envflux = Cache.VesselInfo(vessel).hab_env_flux;
        kerbalflux = Cache.VesselInfo(vessel).hab_kerbal_flux;
        netflux = Cache.VesselInfo(vessel).hab_total_flux;
      }


      // if part is manned (even in the editor), force enabled
      if (Lib.IsManned(part) && state != State.enabled) state = State.equalizing;

      // state machine
      switch(state)
      {
        case State.enabled:
          set_flow(true);
          break;

        case State.disabled:
          set_flow(false);
          break;

        case State.equalizing:
          set_flow(false);
          state = equalize();
          break;

        case State.venting:
          set_flow(false);
          state = venting();
          break;
      }

      // instant pressurization and scrubbing inside breathable atmosphere
      if (!Lib.IsEditor() && Cache.VesselInfo(vessel).breathable && inflate.Length == 0)
      {
        var atmo = part.Resources["Atmosphere"];
        var waste = part.Resources["WasteAtmosphere"];
        if (Features.Pressure) atmo.amount = atmo.maxAmount;
        if (Features.Poisoning) waste.amount = 0.0;
      }
    }


    [KSPEvent(guiActive = true, guiActiveEditor = true, guiName = "_", active = true)]
    public void Toggle()
    {
      // if manned, we can't depressurize
      if (Lib.IsManned(part) && (state == State.enabled || state == State.equalizing))
      {
        Message.Post(Lib.BuildString("Can't disable <b>", Lib.PartName(part), " habitat</b> while crew is inside"));
        return;
      }

      // state switching
      switch(state)
      {
        case State.enabled:     state = State.venting;     break;
        case State.disabled:    state = State.equalizing;  break;
        case State.equalizing:  state = State.venting;     break;
        case State.venting:     state = State.equalizing;  break;
      }
    }


    // action groups
    [KSPAction("Enable/Disable Habitat")] public void Action(KSPActionParam param) { Toggle(); }


    // part tooltip
    public override string GetInfo()
    {
      return Specs().info();
    }


    // specifics support
    public Specifics Specs()
    {
      Specifics specs = new Specifics();
      specs.add("volume", Lib.HumanReadableVolume(volume > double.Epsilon ? volume : Lib.PartVolume(part)));
      specs.add("surface", Lib.HumanReadableSurface(surface > double.Epsilon ? surface : Lib.PartSurface(part)));
      if (inflate.Length > 0) specs.add("Inflatable", "yes");
      return specs;
    }


    // return habitat volume in a vessel in m^3
    public static double tot_volume(Vessel v)
    {
      // we use capacity: this mean that partially pressurized parts will still count,
      return ResourceCache.Info(v, "Atmosphere").capacity;
    }

    // return habitat surface in a vessel in m^2
    public static double tot_surface(Vessel v)
    {
      // we use capacity: this mean that partially pressurized parts will still count,
      return ResourceCache.Info(v, "Shielding").capacity;
    }

    // return normalized pressure in a vessel
    public static double pressure(Vessel v)
    {
      // the pressure is simply the atmosphere level
      return ResourceCache.Info(v, "Atmosphere").level;
    }

    // return waste level in a vessel atmosphere
    public static double poisoning(Vessel v)
    {
      // the proportion of co2 in the atmosphere is simply the level of WasteAtmo
      return ResourceCache.Info(v, "WasteAtmosphere").level;
    }

    // return shielding factor in a vessel
    public static double shielding(Vessel v)
    {
      // the shielding factor is simply the level of shielding, scaled by the 'shielding efficiency' setting
      return ResourceCache.Info(v, "Shielding").level * Settings.ShieldingEfficiency;
    }

    // return living space factor in a vessel
    public static double living_space(Vessel v)
    {
      // living space is the volume per-capita normalized against an 'ideal living space' and clamped in an acceptable range
      return Lib.Clamp((tot_volume(v) / Lib.CrewCount(v)) / Settings.IdealLivingSpace, 0.1, 1.0);
    }

    // return a verbose description of shielding capability
    public static string shielding_to_string(double v)
    {
      return v <= double.Epsilon ? "none" : Lib.BuildString((20.0 * v / Settings.ShieldingEfficiency).ToString("F2"), " mm Pb");
    }

    // traduce living space value to string
    public static string living_space_to_string(double v)
    {
      if (v >= 0.99) return "ideal";
      else if (v >= 0.75) return "good";
      else if (v >= 0.5) return "modest";
      else if (v >= 0.25) return "poor";
      else return "cramped";
    }

    // return habitat temperature in kelvin
    public static double hab_temperature(Vessel v)
    {
      return hab_temperature(ResourceCache.Info(v, "PosWatts").level, ResourceCache.Info(v, "NegWatts").level);
    }

    // return habitat temperature in kelvin
    // PosWatts : full = 295 K, empty = 345 K
    // NegWatts : full = 295 K, empty = 245 K
    public static double hab_temperature(double posWattsLevel, double negWattsLevel)
    {
      if (posWattsLevel < 1.0 && negWattsLevel == 1.0)
      {
        return Settings.TemperatureIdeal + ((1.0 - posWattsLevel) * Settings.TemperatureThreshold * 2.5);
      }
      else if (negWattsLevel < 1.0 && posWattsLevel == 1.0)
      {
        return Settings.TemperatureIdeal - ((1.0 - negWattsLevel) * Settings.TemperatureThreshold * 2.5);
      }
      else
      {
        return Settings.TemperatureIdeal;
      }
    }

    // return difference between ideal temperature and habitat temperature, minus the threshold
    public static double temperature_modifier(double hab_temperature)
    {
      return (hab_temperature - Settings.TemperatureIdeal) > 0 ?
        Math.Max(hab_temperature - Settings.TemperatureIdeal - Settings.TemperatureThreshold, 0.0) :
        Math.Min(hab_temperature - Settings.TemperatureIdeal + Settings.TemperatureThreshold, 0.0);
    }

    public static double above_ideal_pos_modifier(double negWattsLevel, double hab_total_flux)
    {
      return (negWattsLevel == 1.0 && hab_total_flux > 0) ? hab_total_flux : 0.0;
    }

    public static double above_ideal_neg_modifier(double posWattsLevel, double hab_total_flux)
    {
      return (posWattsLevel == 1.0 && hab_total_flux > 0) ? hab_total_flux : 0.0;
    }

    public static double below_ideal_pos_modifier(double negWattsLevel, double hab_total_flux)
    {
      return (negWattsLevel == 1.0 && hab_total_flux < 0) ? Math.Abs(hab_total_flux) : 0.0;
    }

    public static double below_ideal_neg_modifier(double posWattsLevel, double hab_total_flux)
    {
      return (posWattsLevel == 1.0 && hab_total_flux < 0) ? Math.Abs(hab_total_flux) : 0.0;
    }

    // return net atmospheric related heat flux for the vessel habitat (Watt)
    public static double atmo_flux(Vessel v, CelestialBody mainBody, double altitude, double surface, double env_temperature, double hab_temperature)
    {
      // very approximate conductive/convective heat transfer when in atmo (W)
      // Assumption : heat transfer coeficient at 100 kPa is 10 W/m²/K = 0.1 W/m²/K/kPA
      if (mainBody.atmosphere && altitude < mainBody.atmosphereDepth)
      {
        // when flying, use hottest habitat part temperature if there is a signifiant (> 50K) difference with our calculated environnement temperature
        // used to account for reentry / aero heat
        // at high timewarp speeds, stock heat calculations are unreliable, so we don't use them when landed
        if (v != null && v.loaded && !v.Landed)
        {
          List<Part> habitat = v.Parts.FindAll(p => p.Modules.Contains<Habitat>());
          if (habitat.Count > 0)
          {
            double loaded_temperature = habitat.Max(p => p.temperature);
            if (Math.Abs(loaded_temperature - env_temperature) > 50)
            {
              return (loaded_temperature - hab_temperature) * surface * mainBody.GetPressure(altitude) * 0.1;
            }
          }
        }
        return (env_temperature - hab_temperature) * surface * mainBody.GetPressure(altitude) * 0.1;
      }
      return 0.0;
    }

    // return net environnement related heat flux for the vessel habitat (watt/s)
    // not entirely realistic but balanced for the following goals :
    // - around 0.05 ec/s required for a Mk1-2 command pod in kerbin orbit
    // - radiators are required in the inner system (Eve, Moho)
    // - heating is required in the outer system (Duna and beyond)
    public static double env_flux(double hab_surface, double surface_temperature, double hab_temperature, double env_flux)
    {
      const double exposedSurfaceFactor = 0.25;
      const double habAbsorptivity = 0.175;
      const double habEmissivity = 0.145;
      const double habEmissivityFeedback =  0.050;

      return
        // incoming flux for exposed surface from solar + albedo + body + background flux 
        (env_flux * hab_surface * exposedSurfaceFactor * habAbsorptivity)
        // outgoing radiative flux for exposed surface, using surface temperature
        - (PhysicsGlobals.StefanBoltzmanConstant * Math.Pow(surface_temperature, 4.0) * hab_surface * exposedSurfaceFactor * habEmissivity)
        // other outgoing radiative flux, using hab temperature to create a self-regulating feedback
        - (PhysicsGlobals.StefanBoltzmanConstant * Math.Pow(hab_temperature, 4.0) * hab_surface * habEmissivityFeedback);
    }


    // return heat flux due to crew bodies (watt/s)
    public static double kerbal_flux(int crew_count)
    {
      return crew_count * Settings.KerbalHeat; // kerbal bodies heat production
    }

    // habitat state
    public enum State
    {
      disabled,   // hab is disabled
      enabled,    // hab is enabled
      equalizing, // hab is equalizing (between disabled and enabled)
      venting     // hab is venting (between enabled and disabled)
    }

    // constants
    const double equalize_speed = 0.1; // equalization/venting speed per-second, in proportion to volume
  }



} // KERBALISM


