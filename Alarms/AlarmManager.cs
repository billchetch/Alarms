using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Chetch.Messaging;
using Chetch.ChetchXMPP;
using System.ComponentModel;
using System.Text.Json.Serialization;
using Chetch.Utilities;

namespace Chetch.Alarms;

public class AlarmManager
{
    public const int NO_CODE = 0;
    public const int CODE_START_TEST = 1;
    public const int CODE_END_TEST = 2;

    public const int CODE_CONNECTING = 3;

    public const String MESSAGE_FIELD_ALARMS_LIST = "Alarms";
    public const String MESSAGE_FIELD_ALARM = "Alarm";
    
    public const String COMMAND_LIST_ALARMS = "list-alarms";
    public const String COMMAND_TEST_ALARM = "test-alarm";

    static public AlarmState GetRandomRaisedState()
    {
        Array values = Enum.GetValues(typeof(AlarmManager.AlarmState));
        Random random = new Random();
        int idx = random.Next(3, values.Length);
        var obj = values.GetValue(idx);
        return obj == null ? AlarmState.CRITICAL : (AlarmState)obj;
    }

    public enum AlarmState
    {
        DISABLED,
        DISCONNECTED,
        LOWERED,
        MINOR,
        MODERATE,
        SEVERE,
        CRITICAL,
    }

    //Users of this library implement this interface
    public interface IAlarmRaiser
    {
        AlarmManager AlarmManager { get; set; }

        void RegisterAlarms();
    }

    public class Alarm
    {
        static private bool isRaisingState(AlarmState state)
        {
            return state > AlarmState.LOWERED;
        }

        public String ID { get; set; } = String.Empty;

        public String? Name { get; set; }

        public String? Source { get; set; } 

        private AlarmState _state = AlarmState.DISCONNECTED;

        public AlarmState State
        {
            get
            {
                return _state;
            }
            set
            {
                //do some state checking here
                if(IsDisabled && value != AlarmState.DISCONNECTED)
                {
                    throw new Exception(String.Format("Alarm {0} is disabled cannot set state directly to {1}", ID, value));
                }
                if(value == AlarmState.DISABLED && !CanDisable)
                {
                    throw new Exception(String.Format("Alarm {0} cannot be disabled", ID));
                }
                
                bool changed = _state != value;
                var oldState = _state;
                _state = value;
                if (!IsTesting && changed)
                {
                    if (IsRaised)
                    {
                        LastRaised = DateTime.Now;
                        LastLowered = null;
                    }
                    else if (IsLowered && isRaisingState(oldState))
                    {
                        LastLowered = DateTime.Now;
                    }
                    else if (IsDisabled)
                    {
                        LastDisabled = DateTime.Now;
                    }
                }
            }
        }

        [JsonIgnore]
        public IAlarmRaiser? Raiser { get; set; }

        public bool Testing { get; internal set; } = false;

        [JsonIgnore]
        public bool IsTesting => Testing || Code == CODE_START_TEST || Code == CODE_END_TEST;

        [JsonIgnore]
        public bool IsLowered => State == AlarmState.LOWERED;

        [JsonIgnore]
        public bool IsDisabled => State == AlarmState.DISABLED;

        [JsonIgnore]
        public bool IsConnected => State != AlarmState.DISCONNECTED && !IsDisabled;

        [JsonIgnore]
        public bool IsRaised => isRaisingState(State);

        public bool CanDisable { get; set; } = true;

        public String Message { get; set; } = String.Empty;

        public int Code { get; set; }

        public DateTime? LastRaised { get; set; }

        public DateTime? LastLowered { get; set; }

        public DateTime? LastDisabled { get; set; }


        [JsonConstructor]
        public Alarm(){}

        public Alarm(String alarmID)
        {
            ID = alarmID;
        }

        public bool Update(AlarmState state, String? message = null, int code = NO_CODE)
        {
            bool changed = state != State;
            State = state;
            Message = message == null ? String.Empty : message;
            if (!changed) changed = code != Code;
            Code = code;
            return changed;
        }

        public bool StartTest(AlarmState state, String msg = "Start testing", int code = CODE_START_TEST)
        {
            Testing = true;
            return Raise(state, msg, code);
        }

        public bool EndTest(String msg = "End testing", int code = CODE_END_TEST)
        {
            bool changed = Lower(msg, code);
            Testing = false;
            return changed;
        }

        public bool Raise(AlarmState state, String message, int code = NO_CODE)
        {
            if (state == AlarmState.DISCONNECTED || state == AlarmState.DISABLED)
            {
                throw new ArgumentException(String.Format("Alarm state {0} is not valid for raising an alarm", state));
            }
            return Update(state, message, code);
        }

        public bool Lower(String message, int code = NO_CODE)
        {
            return Update(AlarmState.LOWERED, message, code);
        }

        public bool Disccounect(String message, int code = NO_CODE)
        {
            return Update(AlarmState.DISCONNECTED, message, code);
        }

        public void Enable(bool enable = true)
        {
            if (enable)
            {
                if (State == AlarmState.DISABLED)
                {
                    Update(AlarmState.DISCONNECTED);
                }
            }
            else if(State != AlarmState.DISABLED)
            {
                Update(AlarmState.DISABLED);
            }
        }

        public void Disable()
        {
            Enable(false);
        }
    }

    DispatchQueue<Alarm> alarmQueue = [];

    public event EventHandler<Alarm> AlarmChanged;

    public event EventHandler<Alarm> AlarmDequeued;

    public List<IAlarmRaiser> AlarmRaisers { get; internal set; } = new List<IAlarmRaiser>();
    private Dictionary<String, Alarm> _alarms = new Dictionary<String, Alarm>();

    public List<Alarm> Alarms { get => _alarms.Values.ToList(); }

    public Dictionary<String, AlarmState> AlarmStates
    {
        get
        {
            Dictionary<String, AlarmState> alarmStates = new Dictionary<string, AlarmState>();

            foreach(var a in Alarms)
            {
                alarmStates[a.ID] = a.State;
            }
            return alarmStates;
        }
    }

    public Dictionary<String, String> AlarmMessages
    {
        get
        {
            Dictionary<String, String> alarmMessages = new Dictionary<string, String>();

            foreach (var a in Alarms)
            {
                alarmMessages[a.ID] = a.Message;
            }
            return alarmMessages;
        }
    }

    public Dictionary<String, int> AlarmCodes
    {
        get
        {
            Dictionary<String, int> alarmCodes = new Dictionary<string, int>();

            foreach (var a in Alarms)
            {
                alarmCodes[a.ID] = a.Code;
            }
            return alarmCodes;
        }
    }

    public bool IsAlarmRaised
    {
        get
        {
            foreach (Alarm a in _alarms.Values)
            {
                if (a.IsRaised) return true;
            }
            return false;
        }
    }

    private Alarm alarmUnderTest;

    public bool IsTesting => alarmUnderTest != null && alarmUnderTest.IsTesting;

    public AlarmManager()
    {
        alarmQueue.Dequeued += (sender, alarm) => {
            AlarmDequeued?.Invoke(this, alarm);
        };
    }

    public Alarm RegisterAlarm(IAlarmRaiser raiser, String alarmID, String alarmName = null)
    {
        if (raiser == null)
        {
            throw new ArgumentNullException("Raiser cannot be null");
        }

        if (alarmID == null)
        {
            throw new ArgumentNullException("Alarm ID cannot be null");
        }

        if (_alarms.ContainsKey(alarmID))
        {
            throw new Exception(String.Format("There is already an alarm with ID {0}", alarmID));
        }
        
        Alarm alarm = new Alarm(alarmID);
        alarm.Raiser = raiser;
        alarm.Name = alarmName;

        _alarms[alarmID] = alarm;
        return alarm;
    }

    public void DeregisterAlarm(String alarmID)
    {
        Lower(alarmID, "Deregistering alarm {0}");
        _alarms.Remove(alarmID);
    }

    /*public AlarmRaiser AddRaiser(String alarmID, String alarmName, String source)
    {
        var ar = new AlarmRaiser(alarmID, alarmName, source);
        AddRaiser(ar);
        return ar;
    }*/

    public void AddRaiser(IAlarmRaiser raiser)
    {
        if (!AlarmRaisers.Contains(raiser))
        {
            AlarmRaisers.Add(raiser);
            raiser.AlarmManager = this;
            raiser.RegisterAlarms();
        }
    }


    public void AddRaisers(IEnumerable<Object> items)
    {
        foreach(var item in items)
        {
            if(item is IAlarmRaiser)
            {
                AddRaiser((IAlarmRaiser)item);
            }
        }
    }

    public void RemoveRaisers()
    {
        var alarms2remove = _alarms.Keys.ToList();
        foreach(var alarmID in alarms2remove)
        {
            //Deregister will Lower the larm first
            DeregisterAlarm(alarmID);
        }
        AlarmRaisers.Clear();
    }

    public Alarm GetAlarm(String id, bool throwException = false)
    {
        if(throwException && !_alarms.ContainsKey(id))
        {
            throw new Exception(String.Format("Alarm {0} not found", id));
        }
        return _alarms.ContainsKey(id) ? _alarms[id] : null;
    }

    public bool HasAlarm(String id)
    {
        return _alarms.ContainsKey(id);
    }

    public bool HasAlarmWithState(AlarmState alarmState)
    {

        foreach (Alarm a in _alarms.Values)
        {
            if (a.State == alarmState) return true;
        }
        return false;
    }

    public bool IsAlarmDisabled(String alarmID)
    {
        Alarm alarm = GetAlarm(alarmID, true);
        return alarm.IsDisabled;
    }

    public Alarm UpdateAlarm(String alarmID, AlarmState alarmState, String? alarmMessage, int code = NO_CODE)
    {
        Alarm alarm = GetAlarm(alarmID, true);
        bool changed = alarm.Update(alarmState, alarmMessage, code);

        if(AlarmChanged != null && changed)
        {
            alarmQueue.Enqueue(alarm);
            AlarmChanged.Invoke(this, alarm);
        }

        return alarm;
    }
    
    public Alarm Raise(String alarmID, AlarmState alarmState, String alarmMessage, int code = NO_CODE)
    {
        //Check this is a 'raising' alarm state
        if(alarmState == AlarmState.DISCONNECTED || alarmState == AlarmState.LOWERED || alarmState == AlarmState.DISABLED)
        {
            throw new ArgumentException(String.Format("Alarm state {0} is not valid for raising an alarm", alarmState));
        }

        return UpdateAlarm(alarmID, alarmState, alarmMessage);
    }

    public Alarm Lower(String alarmID, String alarmMessage, int code = NO_CODE)
    {
        return UpdateAlarm(alarmID, AlarmState.LOWERED, alarmMessage, code);
    }

    
    public Alarm Enable(String alarmID)
    {
        return UpdateAlarm(alarmID, AlarmState.DISCONNECTED, null);
    }

    public Alarm Disable(String alarmID)
    {
        return UpdateAlarm(alarmID, AlarmState.DISABLED, null);
    }

    public Alarm StartTest(String alarmID, AlarmState alarmState, String alarmMessage, int code = NO_CODE)
    {
        if (IsTesting)
        {
            throw new Exception(String.Format("Cannot test {0} as {1} is already being tested", alarmID, alarmUnderTest.ID));
        }

        var alarm = GetAlarm(alarmID, true);
        if (alarm.IsRaised)
        {
            throw new Exception(String.Format("Alarm {0} already raised", alarmID));
        }
        if(alarm.State == AlarmState.DISCONNECTED)
        {
            throw new Exception(String.Format("Alarm {0} is disconnected", alarmID));
        }

        try
        {
            alarmUnderTest = alarm;
            bool changed = alarm.StartTest(alarmState, alarmMessage, code);

            if (AlarmChanged != null && changed)
            {
                AlarmChanged.Invoke(this, alarm);
            }
        } catch (Exception e)
        {
            alarmUnderTest.EndTest();
            alarmUnderTest = null;
            throw new Exception(String.Format("AlarmManager::StartTest throws {0} for alarm {1}", e.Message, alarmID), e);
        }
        return alarm;
    }

    public Alarm EndTest()
    {
        if (!IsTesting)
        {
            return null;
        }
        var alarm = alarmUnderTest;
        alarmUnderTest = null;

        bool changed = alarm.EndTest();
        if (AlarmChanged != null && changed)
        {
            AlarmChanged.Invoke(this, alarm);
        }
        
        return alarm;
    }

    public void RunTest(String alarmID, AlarmState alarmState, String alarmMessage, int duration, int code  = NO_CODE)
    {
        StartTest(alarmID, alarmState, alarmMessage, code);
        var task = Task.Run(() =>
        {
            Thread.Sleep(duration);
            EndTest();
        });
    }


    public void Connect(IAlarmRaiser? raiser = null)
    {
        foreach(var alarm in _alarms.Values)
        {
            if(!alarm.IsConnected && (raiser == null || alarm.Raiser == raiser) && !alarm.IsDisabled)
            {
                Lower(alarm.ID, String.Format("Connecting {0}", alarm.ID), CODE_CONNECTING);
            }
        }
    }

    public void Connect(String alarmSource) //String alarmID, String alarmMessage, int code = AlarmsMessageSchema.CODE_SOURCE_OFFLINE)
    {

        foreach (var alarm in _alarms.Values)
        {
            if (!alarm.IsConnected && !alarm.IsDisabled && alarm.Source == alarmSource)
            {
                Lower(alarm.ID, String.Format("Connecting {0}", alarm.ID), CODE_CONNECTING);
            }
        }
    }

    
    public void Disconnect(IAlarmRaiser? raiser = null) //String alarmID, String alarmMessage, int code = AlarmsMessageSchema.CODE_SOURCE_OFFLINE)
    {

        foreach (var alarm in _alarms.Values)
        {
            if (alarm.IsConnected && (raiser == null || alarm.Raiser == raiser))
            {
                UpdateAlarm(alarm.ID, AlarmState.DISCONNECTED, String.Format("Disconnecting {0}", alarm.ID), NO_CODE);
            }
        }
    }

    public void Disconnect(String alarmSource) //String alarmID, String alarmMessage, int code = AlarmsMessageSchema.CODE_SOURCE_OFFLINE)
    {

        foreach (var alarm in _alarms.Values)
        {
            if (alarm.IsConnected && alarm.Source == alarmSource)
            {
                UpdateAlarm(alarm.ID, AlarmState.DISCONNECTED, String.Format("Disconnecting {0}", alarm.ID), NO_CODE);
            }
        }
    }


    public void UpdateFromAlertMessage(Message message)
    {
        if(message.Type != MessageType.ALERT)
        {
            throw new Exception(String.Format("AlarmManager::UpdateFromAlertMessage message is of type {0} it must be of type ALERT", message.Type));
        }
        Alarm alarm = message.Get<Alarm>(MESSAGE_FIELD_ALARM);

        if (!HasAlarm(alarm.ID))
        {
            throw new Exception(String.Format("AlarmManager::UpdateFromAlertMessage alarm manager does not have an larm with ID {0}", alarm.ID));
        }
        
        UpdateAlarm(alarm.ID, alarm.State, alarm.Message);
    }

    public Message CreateAlertMessage(Alarm alarm, String? target = null)
    {
        Message alert = ChetchXMPPMessaging.CreateAlertMessage((int)alarm.State);
        if(target != null)
        {
            alert.Target = target;
        }
        alert.AddValue(MESSAGE_FIELD_ALARM, alarm);
        return alert;
    }

    public bool IsAlertMessage(Message message)
    {
        return message.Type == MessageType.ALERT && message.HasValue(MESSAGE_FIELD_ALARM);
    }

    public Message CreateListAlarmsMessage(String target)
    {
        Message command = ChetchXMPPMessaging.CreateCommandMessage(COMMAND_LIST_ALARMS);
        command.Target = target;
        return command;
    }

    public void AddAlarmsListToMessage(Message message)
    {
        message.AddValue(MESSAGE_FIELD_ALARMS_LIST, Alarms);
    }

    public List<Alarm> UpdateFromListAlarmsResponse(Message response)
    {
        if (!response.HasValue(MESSAGE_FIELD_ALARMS_LIST))
        {
            throw new Exception(String.Format("Message does not contain a {0} field", MESSAGE_FIELD_ALARMS_LIST));
        }

        var alarmsList = response.GetList<Alarm>(MESSAGE_FIELD_ALARMS_LIST);
        foreach(var a in alarmsList)
        {
            if (HasAlarm(a.ID))
            {
                UpdateAlarm(a.ID, a.State, a.Message);
            }
        }
        return alarmsList;
    }

    public Task Run(Func<bool> canDequeue, CancellationToken ct)
    {
        alarmQueue.CanDequeue = canDequeue;
        return alarmQueue.Run(ct);
    }

    public Task Run(CancellationToken ct)
    {
        alarmQueue.CanDequeue = () => true;
        return alarmQueue.Run(ct);
    }
}
